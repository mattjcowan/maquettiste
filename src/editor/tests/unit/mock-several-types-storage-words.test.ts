// The mock's storage run on cases the engine's two recorded fixtures have no row for yet. Every expected text is copied from the engine's
// source (DatabaseRun.TypeStorage.cs, MappingRules.cs), not from the mock; the cases are listed in the review so the engine fixture can be
// extended with them later. The base is the engine's run fixture; each case adds a relation and its mapping.
import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { packs } from "@/mocks/model/seed";
import type { SeedFile } from "@/mocks/model/store";

function filesUnder(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? filesUnder(path.join(dir, e.name)) : [path.join(dir, e.name)]));
}

const DB = "01KST2DBS00000000000000001";
const ID = (n: string) => `01KST2${n}`;
const E = { part: ID("ENT00000000000000001"), assembly: ID("ENT00000000000000002"), comment: ID("ENT00000000000000003"), note: ID("ENT00000000000000005") };
const num = (kind: string, n: number) => ID(`${kind}${String(n).padStart(17, "0")}`);
const C = (n: number) => num("CMN", n);
const FK = (n: number) => num("FKY", n);

let seq = 0;
/** The run fixture's files, a relation with one end of several types, and its mapping. */
function run(opts: {
  end: (id: string) => object;
  other?: object;
  relation?: object;
  mapping: (ends: [string, string]) => object;
  dialect?: string;
  extra?: object[];
}) {
  const root = path.resolve(__dirname, "../fixtures/several-types-storage-run");
  const base = path.join(root, ".maquettiste");
  const files: SeedFile[] = filesUnder(base)
    .filter((f) => !f.includes(`${path.sep}.schema${path.sep}`))
    .sort()
    .map((f) => ({ path: path.relative(base, f).split(path.sep).join("/"), text: fs.readFileSync(f, "utf8") }));
  if (opts.dialect) {
    const db = files.find((f) => f.path.endsWith("databases/main/database.json"))!;
    db.text = db.text.replace('"postgresql"', `"${opts.dialect}"`);
  }
  seq += 1;
  const n = String(900 + seq).padStart(5, "0");
  const ends: [string, string] = [num("END", 900 + seq), num("END", 5900 + seq)];
  const relationId = num("RXN", 900 + seq);
  const mappingId = num("MAP", 900 + seq);
  const add = (p: string, json: object) => files.push({ path: p, text: JSON.stringify(json, null, 2) });
  add(`model/relations/zz${n}.json`, {
    kind: "relation",
    id: relationId,
    name: `Case${n}`,
    package: ID("PKG00000000000000001"),
    relationKind: "association",
    ends: [
      { id: ends[0], entity: E.comment, role: "comment", navigation: "", max: "*", ...(opts.other ?? {}) },
      { id: ends[1], max: 1, ...opts.end(ends[1]), role: "attachedTo", navigation: "" },
    ],
    ...(opts.relation ?? {}),
  });
  add(`model/mappings/zz${n}.json`, { kind: "mapping", id: mappingId, name: `Case${n} in main`, database: DB, relation: relationId, ...opts.mapping(ends) });
  for (const [i, x] of (opts.extra ?? []).entries()) {
    const kind = (x as { kind: string }).kind;
    add(`model/${kind === "table" ? "databases/main/tables" : `${kind}s`}/zz${n}-${i}.json`, x);
  }
  const backend = new MockBackend({ seed: { files, packs: packs() } });
  return backend.model
    .validate()
    .diagnostics.filter((d) => d.elementId === mappingId)
    .map((d) => ({ rule: d.rule, pointer: d.jsonPointer ?? "", message: d.message, severity: d.severity }));
}

const partAssembly = () => ({ types: [{ entity: E.part }, { entity: E.assembly }] });

describe("the storage run's words on cases the fixtures lack", () => {
  it("MQ4117: a types item for an entity the end doesn't cover", () => {
    const found = run({
      end: partAssembly,
      mapping: ([, b]) => ({ ends: [{ end: b, types: [{ entity: E.note, foreignKey: FK(4) }] }] }),
    });
    expect(found).toContainEqual({
      rule: "MQ4117",
      pointer: "/ends/0/types/0/entity",
      severity: "error",
      message: "Type 'Note' of a types item isn't covered by end 'attachedTo'; name one of the end's types.",
    });
  });

  it("MQ4117: keys per table at an any-entity end", () => {
    const found = run({
      end: () => ({ anyEntity: true }),
      mapping: ([a]) => ({ ends: [{ end: a, foreignKeys: [FK(4), FK(5)] }] }),
    });
    expect(found.map((f) => [f.rule, f.pointer, f.message])).toContainEqual([
      "MQ4117",
      "/ends/0/foreignKeys",
      "End 'attachedTo' is any entity, so its types' tables can't each hold a key: every new entity would need a column. Use a type and an id, or an id alone.",
    ]);
  });

  it("MQ4117: a referencesColumn outside the target's table", () => {
    const found = run({
      end: partAssembly,
      mapping: ([, b]) => ({
        ends: [
          {
            end: b,
            idColumn: C(13),
            types: [
              { entity: E.part, referencesColumn: C(23) },
              { entity: E.assembly, referencesColumn: C(10) },
            ],
          },
        ],
      }),
    });
    expect(found.map((f) => [f.rule, f.pointer, f.message])).toContainEqual([
      "MQ4117",
      "/ends/0",
      "Column 'note' that end 'attachedTo' refers to for type 'Part' isn't a column of table 'parts'.",
    ]);
  });

  it("MQ4117: a key of another table than the one that holds the links", () => {
    const found = run({
      end: partAssembly,
      mapping: ([a, b]) => ({
        junctionTable: ID("TAB00000000000000008"),
        ends: [
          { end: a, foreignKey: FK(4) },
          { end: b, idColumn: C(33) },
        ],
      }),
    });
    expect(found.map((f) => [f.rule, f.pointer, f.message])).toContainEqual([
      "MQ4117",
      "/ends/0/foreignKey",
      "Foreign key 'fk_part' of table 'comments' points at end 'comment', but the links of 'Comment' there are held in table 'comment_parts'; name a key of that table.",
    ]);
  });

  it("MQ4117: a primary key of several columns names the id column, and the end", () => {
    const table = ID("TAB00000000000000091");
    const found = run({
      end: () => ({ types: [{ entity: ID("ENT00000000000000091") }, { entity: E.part }] }),
      mapping: ([, b]) => ({ ends: [{ end: b, idColumn: C(13) }] }),
      extra: [
        {
          kind: "entity",
          id: ID("ENT00000000000000091"),
          name: "Multi",
          package: ID("PKG00000000000000001"),
          key: { attributes: [ID("ATT00000000000000091")] },
          attributes: [{ id: ID("ATT00000000000000091"), name: "id", type: "uuid" }],
        },
        {
          kind: "mapping",
          id: ID("MAP00000000000000091"),
          name: "Multi in main",
          database: DB,
          entity: ID("ENT00000000000000091"),
          table,
        },
        {
          kind: "table",
          id: table,
          name: "multi",
          database: DB,
          columns: [
            { id: ID("CMN00000000000009101"), name: "a", type: "uuid", nullable: false },
            { id: ID("CMN00000000000009102"), name: "b", type: "uuid", nullable: false },
          ],
          primaryKey: { columns: [ID("CMN00000000000009101"), ID("CMN00000000000009102")] },
        },
      ].map((x) => ({ ...x })),
    });
    expect(found.map((f) => [f.rule, f.pointer, f.message])).toContainEqual([
      "MQ4117",
      "/ends/0/idColumn",
      "Table 'multi' of type 'Multi' has a primary key of 2 columns, so column 'attached_id' of end 'attachedTo' needs 'referencesColumn' naming the unique column it refers to.",
    ]);
  });

  it("MQ4117: a many-to-many relation with an entry on each end and no link table", () => {
    const found = run({
      end: () => ({ ...partAssembly(), max: "*" }),
      mapping: ([a, b]) => ({
        ends: [
          { end: a, idColumn: C(13) },
          { end: b, idColumn: C(13) },
        ],
      }),
    });
    expect(found.map((f) => [f.rule, f.pointer, f.message])).toContainEqual([
      "MQ4117",
      "/ends",
      "Relation 'Case" +
        String(900 + seq).padStart(5, "0") +
        "' is many to many, so its links need a link table: name 'junctionTable', or a key per type of one end in tables of their own.",
    ]);
  });

  it("MQ4118: the delete rule joins the no-foreign-key clause, and names are joined with 'or'", () => {
    const found = run({
      end: () => ({ ...partAssembly(), onDelete: "cascade" }),
      mapping: ([, b]) => ({ ends: [{ end: b, idColumn: C(13), typeColumn: C(12) }] }),
    });
    expect(found.find((f) => f.rule === "MQ4118")?.message).toBe(
      "Database 'main' holds end 'attachedTo' of relation 'Case" +
        String(900 + seq).padStart(5, "0") +
        "' as a type and an id in table 'comments', so it can't refuse a link to a missing 'Part' or 'Assembly', nor apply the delete rule 'cascade'; and it finds no check that names column 'attached_type' of table 'comments'. To check the codes, add: CONSTRAINT ck_comments_attached_to CHECK (attached_type IN ('Assembly', 'Part'))",
    );
  });

  it("MQ4118: on SQL Server the codes are N'' literals and a one-to-one unique is a filtered index", () => {
    const found = run({
      dialect: "sqlserver",
      end: partAssembly,
      other: { max: 1 },
      mapping: ([, b]) => ({ ends: [{ end: b, idColumn: C(13), typeColumn: C(12) }] }),
    });
    const message = found.find((f) => f.rule === "MQ4118")?.message ?? "";
    expect(message).toContain("CHECK (attached_type IN (N'Assembly', N'Part'))");
    expect(message).toMatch(
      /CREATE UNIQUE INDEX ix_comments_attached_type_attached_id ON (\w+\.)?comments \(attached_type, attached_id\) WHERE attached_type IS NOT NULL AND attached_id IS NOT NULL/,
    );
  });

  it("MQ4009: two types items for one entity", () => {
    const found = run({
      end: partAssembly,
      mapping: ([, b]) => ({
        ends: [
          {
            end: b,
            types: [
              { entity: E.part, foreignKey: FK(4) },
              { entity: E.part, foreignKey: FK(5) },
            ],
          },
        ],
      }),
    });
    expect(found.filter((f) => f.rule === "MQ4009")).toEqual([
      {
        rule: "MQ4009",
        pointer: "/ends/0/types/1/entity",
        severity: "error",
        message: "End entry 0 of relation 'Case" + String(900 + seq).padStart(5, "0") + "' lists entity 'Part' in two types items; keep one.",
      },
    ]);
  });

  it("MQ4009: constants with no storage give one finding", () => {
    const found = run({
      end: partAssembly,
      mapping: () => ({ constants: [{ column: C(12), value: "A" }] }),
    });
    expect(found.filter((f) => f.rule === "MQ4009").map((f) => f.pointer)).toEqual(["/constants"]);
  });
});
