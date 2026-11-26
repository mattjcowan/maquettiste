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
  printCondition,
  removeOwnCopyEntry,
  removeOwnPart,
  setCopyWord,
  setPartAttributeWord,
  setPartChoice,
  setPartWord,
  type Json,
} from "@/editors/copyEdit";
import {
  hostOf,
  inheritedTypeColumn,
  isIntegerColumn,
  readCodes,
  setTypeCode,
  setTypeColumn,
  typeColumnOwner,
  typeColumnRole,
} from "@/editors/storage/typeColumn";
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

  it("offers `original` as the engine allows: one key attribute, same kind, at least as wide, never a value object; the nearest key counts", () => {
    const money = (id: string) => ({ "type-int": "int32" })[id];
    const entity: Json = {
      id: "E",
      key: { attributes: ["id"] },
      attributes: [
        attr("id", { type: "int32", required: true }),
        attr("wide", { type: "int64" }),
        attr("narrow", { type: "int16" }),
        attr("custom", { type: { ref: "type-int" } }),
        attr("vo", { type: { ref: "some-value-object" } }),
        attr("list", { type: "int32", collection: true }),
        attr("text"),
      ],
    };
    const words = (doc: Json) => Object.fromEntries(copyAttributes([doc], money).map((a) => [a.name, a.words.includes("original")]));
    expect(words(entity)).toEqual({ wide: true, narrow: false, custom: true, vo: false, list: false, text: false });
    // Two key attributes: none can hold the id.
    expect(words({ ...entity, key: { attributes: ["id", "text"] } })).toMatchObject({ wide: false, custom: false });
    // A derived entity without a key of its own reads the base's; its own key wins when it has one.
    const derived: Json = { id: "D", base: "E", attributes: [attr("also", { type: "int32" })] };
    expect(copyAttributes([derived, entity], money).find((a) => a.name === "also")?.words).toContain("original");
    expect(copyAttributes([{ ...derived, key: { attributes: ["also"] } }, entity], money).map((a) => a.name)).not.toContain("also");
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

  it("writes `none` and `all` in the simplest form, and `one` only with its choice", () => {
    const entity: Json = { id: "E" };
    setPartWord(entity, "p", "none", undefined);
    expect(entity.copy).toEqual({ parts: [{ end: "p", onCopy: "none" }] });
    setPartWord(entity, "p", "all", undefined);
    expect(entity.copy).toBeUndefined();

    // `one` without a choice is never written (MQ3047).
    setPartWord(entity, "p", "one", undefined);
    expect(entity.copy).toBeUndefined();
    expect(setPartChoice(entity, "p", undefined, {})).toBe(false);
    expect(entity.copy).toBeUndefined();

    expect(setPartChoice(entity, "p", undefined, { state: "S1", latestBy: "version" })).toBe(true);
    expect(entity.copy).toEqual({ parts: [{ end: "p", onCopy: "one", choose: { state: "S1", latestBy: "version" } }] });
    setPartChoice(entity, "p", undefined, { condition: " current = true ", latestBy: "version" });
    expect((entity.copy as { parts: Json[] }).parts[0].choose).toEqual({ condition: { text: "current = true" }, latestBy: "version" });
    expect(choiceOf((entity.copy as { parts: Json[] }).parts[0])).toEqual({ condition: { text: "current = true" }, latestBy: "version" });
    // An empty choice leaves the written one alone: `choose` is never dropped while the entry is `one`.
    expect(setPartChoice(entity, "p", undefined, {})).toBe(false);
    expect((entity.copy as { parts: Json[] }).parts[0]).toMatchObject({ onCopy: "one", choose: { latestBy: "version" } });
    // Changing from a condition to the latest alone saves.
    setPartChoice(entity, "p", undefined, { latestBy: "version" });
    expect((entity.copy as { parts: Json[] }).parts[0].choose).toEqual({ latestBy: "version" });
    // Leaving `one` drops the choice.
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
    expect(isOrderable("float", base)).toBe(false);
    expect(isOrderable({ ref: "money" }, base)).toBe(true);
    expect(isOrderable({ ref: "blob" }, base)).toBe(false);
    expect(isOrderable({ ref: "unknown" }, base)).toBe(false);
  });

  it("takes an own entry back to the base's, for a part, a field and a link", () => {
    const entity: Json = {
      id: "E",
      copy: { attributes: [{ attribute: "a", onCopy: "copy" }], links: [{ end: "x", onCopy: "copy" }], parts: [{ end: "p", onCopy: "all" }] },
    };
    removeOwnPart(entity, "p");
    removeOwnCopyEntry(entity, "attributes", "a");
    expect(entity.copy).toEqual({ links: [{ end: "x", onCopy: "copy" }] });
    removeOwnCopyEntry(entity, "links", "x");
    expect(entity.copy).toBeUndefined();
  });

  it("lists the relations of a part's owner in name order, as the engine walks them", () => {
    const names = ["b", "a"].map(
      (name) =>
        ({
          id: name,
          kind: "relation",
          name,
          ends: [
            { entity: "O", role: "o" },
            { entity: `P${name}`, role: name },
          ],
        }) as unknown as ElementSummary,
    );
    const docs = new Map<string, Json>(
      names.map((r) => [
        r.id,
        {
          id: r.id,
          name: r.name,
          relationKind: "composition",
          ends: [
            { id: `${r.id}o`, role: "o", max: 1 },
            { id: `${r.id}p`, role: r.name, max: "*" },
          ],
        },
      ]),
    );
    expect(
      partTree(
        "O",
        () => names,
        docs,
        () => undefined,
      ).map((p) => p.role),
    ).toEqual(["a", "b"]);
  });
});

describe("a condition as text", () => {
  const names = { name: (id: string) => ({ a: "quantity", b: "status", c: "current", draft: "Draft" })[id] };
  const op = (o: string, ...args: unknown[]) => ({ op: o, args });
  const path = (id: string) => ({ path: [id] });

  it("keeps precedence with parentheses", () => {
    expect(printCondition(op("and", op("or", op("gt", path("a"), { value: 1 }), path("c")), path("c")), names)).toBe("(quantity > 1 or current) and current");
    expect(printCondition(op("or", op("and", path("c"), path("c")), path("c")), names)).toBe("current and current or current");
    expect(printCondition(op("sub", path("a"), op("sub", path("a"), { value: 1 })), names)).toBe("quantity - (quantity - 1)");
    expect(printCondition(op("not", op("eq", path("a"), { value: 1 })), names)).toBe("not (quantity = 1)");
    expect(printCondition(op("eq", path("b"), { ref: "draft" }), names)).toBe("status = Draft");
  });

  it("says no to a tree it can't write instead of rewriting it", () => {
    expect(printCondition({ case: [] }, names)).toBeNull();
    expect(printCondition(op("eq", path("missing"), { value: 1 }), names)).toBeNull();
    expect(printCondition({ text: "current = true" }, names)).toBe("current = true");
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
  const doc = (id: string, bindings: Binding[], base?: string): [string, Json] => [id, { id, name: id, ...(base ? { base } : {}), bindings }];
  const own = (m: Map<string, Json>, id: string) => (m.get(id)!.bindings as Binding[])[0];
  const docs = new Map<string, Json>([
    doc("Note", [binding({ discriminatorColumn: "kind", discriminatorValue: "note" })]),
    doc("Urgent", [binding({ discriminatorValue: "urgent" })], "Note"),
    doc("Solo", [binding()]),
  ]);

  it("writes the column by its key, and the code as text, or as a number on an integer column", () => {
    const b = binding();
    setTypeColumn(b, column("k1", "kind"));
    setTypeCode(b, " note ");
    expect(b).toMatchObject({ discriminatorColumn: "k1", discriminatorValue: "note" });
    setTypeCode(b, "1");
    expect(b.discriminatorValue).toBe("1");
    setTypeCode(b, "2", true);
    expect(b.discriminatorValue).toBe(2);
    // A number stays a number; text that isn't a whole number stays text.
    setTypeCode(b, "3");
    expect(b.discriminatorValue).toBe(3);
    setTypeCode(b, "x", true);
    expect(b.discriminatorValue).toBe("x");
    setTypeColumn(b, null);
    setTypeCode(b, "");
    expect(b).toEqual({ id: "b", database: "db", source: "t" });
    expect(["int", "int32", "integer", "bigint", "smallint", "int4", "tinyint(1)"].map(isIntegerColumn)).toEqual([true, true, true, true, true, false, true]);
    expect(["string", "varchar(20)", "uuid", null].map((t) => isIntegerColumn(t))).toEqual([false, false, false, false]);
  });

  it("hides the controls outside a hierarchy, lets a base name the column, and shows a derived entity the column from its base", () => {
    expect(typeColumnRole("Solo", binding(), docs, false)).toEqual({ role: "none" });
    expect(typeColumnRole("Note", binding(), docs, true)).toEqual({ role: "base" });
    expect(typeColumnRole("Urgent", binding(), docs, false)).toEqual({ role: "derived", column: "kind", base: "Note", redundant: false });
    expect(inheritedTypeColumn("Urgent", binding({ database: "other-db" }), docs)).toBeUndefined();
    // Another database: the base's binding there is none, so the derived entity may name its own.
    expect(typeColumnRole("Urgent", binding({ database: "other-db" }), docs, false)).toEqual({ role: "base" });
  });

  it("lets a derived entity name its own column when no base names one, and when it keeps its rows in another table", () => {
    // Root -> Mid -> Leaf; Mid and Leaf share t2 and Mid names the column: Mid may name it, Leaf reads it from Mid.
    const a = new Map<string, Json>([
      doc("Root", [binding({ source: "t1" })]),
      doc("Mid", [binding({ source: "t2", discriminatorColumn: "kind" })], "Root"),
      doc("Leaf", [binding({ source: "t2" })], "Mid"),
    ]);
    expect(typeColumnRole("Mid", own(a, "Mid"), a, true)).toEqual({ role: "base" });
    expect(typeColumnRole("Leaf", binding({ source: "t2" }), a, false)).toEqual({ role: "derived", column: "kind", base: "Mid", redundant: false });
    expect(typeColumnOwner("Leaf", "db", a)).toBe("Mid");
    // Sub keeps its rows in another table than its base: the base's column isn't inherited, and Sub may name its own.
    const b = new Map<string, Json>([
      doc("Base", [binding({ source: "t1", discriminatorColumn: "k" })]),
      doc("Sub", [binding({ source: "t2", discriminatorColumn: "own" })], "Base"),
    ]);
    expect(typeColumnRole("Sub", own(b, "Sub"), b, false)).toEqual({ role: "base" });
    expect(typeColumnOwner("Sub", "db", b)).toBe("Sub");
    // The same table: naming a column too is refused, and the card says the base's applies.
    const c = new Map<string, Json>([doc("Base", [binding({ discriminatorColumn: "k" })]), doc("Sub", [binding({ discriminatorColumn: "own" })], "Base")]);
    expect(typeColumnRole("Sub", own(c, "Sub"), c, false)).toEqual({ role: "derived", column: "k", base: "Base", redundant: true });
    expect(typeColumnOwner("Sub", "db", c)).toBe("Base");
  });

  it("keeps a binding's rows in the table it writes, else its source", () => {
    expect(hostOf(binding({ source: "t" }))).toBe("t");
    expect(hostOf(binding({ source: "t", write: { table: "w" } }))).toBe("w");
    expect(hostOf(binding({ source: "v", write: "none" }))).toBe("v");
  });

  it("lists each code once: its own and those of the entities derived from it in the same table with the same column owner", () => {
    const all = new Map<string, Json>([
      ...docs,
      doc("Deep", [binding()], "Urgent"),
      doc("Dup", [binding({ discriminatorValue: "urgent" })], "Note"),
      doc("Elsewhere", [binding({ source: "other-table" })], "Note"),
      doc("Written", [binding({ source: "v", write: { table: "t" } })], "Note"),
    ]);
    const derived = ["Urgent", "Deep", "Dup", "Elsewhere", "Written"];
    expect(readCodes("Note", own(docs, "Note"), derived, all)).toEqual(["note", "urgent", "Deep", "Written"]);
    expect(readCodes("Urgent", binding({ discriminatorValue: "urgent" }), ["Deep"], all)).toEqual(["urgent", "Deep"]);
    // No column anywhere: no other entity's rows are shared.
    expect(readCodes("Solo", binding(), ["Deep"], all)).toEqual(["Solo"]);
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
