// The mock's named queries (queries.ts and querySql.ts mirror DatabaseRun.NamedQueries.cs and Rendering/QuerySql.cs): resolution,
// the WITH clause per dialect, a collection repeating the entries it reads, a binding over the query (WITH first), every MQ4099 case
// at the engine's pointer and message, MQ3001/MQ4021/MQ4023 for named queries, and the editor's renames across the document.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { resolveQueries } from "@/mocks/model/queries";
import { bindingSql } from "@/mocks/model/bindings";
import { renderQuery, splitWith } from "@/mocks/model/querySql";
import { bindingProblems, sourceOf } from "@/editors/storage/fieldMap";
import { namedOptions, renameNamedField, renameNamedQuery, scopeOf, type NamedQuery, type QueryDoc } from "@/model/queryTree";
import type { DatabaseView } from "@/api/types";

type Json = Record<string, unknown>;

const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
const INVOICES = `${INVOICE}@${MAIN}`;
const LINES = `01J92P0V0GWFR78HZH0P8Z3GY7@${MAIN}`;
const QUERY = "01K6QRY0000000000000000099";

const chain = (extra: Json = {}): Json => ({
  name: "chain",
  from: { source: INVOICES, alias: "c" },
  select: [
    { name: "id", expression: { column: "c.id" } },
    { name: "depth", expression: { value: 0 } },
  ],
  where: { op: "eq", left: { column: "c.id" }, right: { param: "invoiceId" } },
  recursive: {
    from: { source: "chain", alias: "ch" },
    joins: [{ source: INVOICES, alias: "p", on: { op: "eq", left: { column: "p.id" }, right: { column: "ch.id" } } }],
    select: [
      { name: "id", expression: { column: "p.id" } },
      { name: "depth", expression: { op: "+", args: [{ column: "ch.depth" }, { value: 1 }] } },
    ],
  },
  ...extra,
});

const query = (extra: Json = {}): Json => ({
  id: QUERY,
  name: "Q",
  parameters: [{ name: "invoiceId", type: "uuid" }],
  with: [chain()],
  from: { source: "chain", alias: "x" },
  select: [
    { name: "id", expression: { column: "x.id" } },
    { name: "depth", expression: { column: "x.depth" } },
  ],
  ...extra,
});

function bind(json: Json, dialect?: string) {
  const backend = new MockBackend();
  const view = { ...backend.generation.databaseView(MAIN)!.view!, ...(dialect ? { dialect } : {}) } as DatabaseView;
  const docs = new Map(backend.model.docs());
  docs.set(QUERY, { kind: "query", database: MAIN, ...json });
  const result = resolveQueries(view, docs);
  const found = result.diagnostics.filter((d) => d.elementId === QUERY);
  return { result, view, docs, found, rules: found.map((d) => [d.rule, d.jsonPointer ?? "", d.message]), q: result.resolved.get(QUERY)! };
}

describe("named queries: rendering", () => {
  it("renders a recursive named query as WITH RECURSIVE with its column list, anchor, UNION ALL and repeating part", () => {
    const { found, q } = bind(query());
    expect(found).toEqual([]);
    expect(q.sql).toBe(
      [
        "WITH RECURSIVE chain (id, depth) AS (",
        "    SELECT c.id AS id, 0 AS depth",
        "    FROM billing.invoices c",
        "    WHERE c.id = @invoiceId",
        "    UNION ALL",
        "    SELECT p.id AS id, (ch.depth + 1) AS depth",
        "    FROM chain ch",
        "    INNER JOIN billing.invoices p ON p.id = ch.id",
        ")",
        "SELECT x.id AS id, x.depth AS depth",
        "FROM chain x",
      ].join("\n"),
    );
  });

  it("writes WITH without RECURSIVE on SQL Server and Oracle, WITH RECURSIVE on MySQL and SQLite, and UNION for distinct rows", () => {
    const sql = (dialect: string, entry: Json = chain()) => renderQuery(bind(query({ with: [entry] })).q, dialect).sql.split("\n")[0];
    expect(sql("sqlserver")).toMatch(/^WITH chain/);
    expect(sql("oracle")).toMatch(/^WITH chain/);
    expect(sql("mysql")).toMatch(/^WITH RECURSIVE chain/);
    expect(sql("sqlite")).toMatch(/^WITH RECURSIVE chain/);
    const distinct = chain({ recursive: { ...(chain().recursive as Json), union: "distinct" } });
    expect(renderQuery(bind(query({ with: [distinct] })).q, "postgresql").sql).toContain("\n    UNION\n");
    // A plain named query is WITH, never RECURSIVE.
    const plain = chain();
    delete plain.recursive;
    expect(renderQuery(bind(query({ with: [plain] })).q, "postgresql").sql.split("\n")[0]).toBe("WITH chain (id, depth) AS (");
  });

  it("joins several entries with a comma, indents an exists inside an entry eight spaces, and numbers $ placeholders in text order", () => {
    const exists = {
      name: "early",
      from: { source: INVOICES, alias: "i" },
      select: [{ name: "id", expression: { column: "i.id" } }],
      where: {
        and: [
          { op: "eq", left: { column: "i.id" }, right: { param: "invoiceId" } },
          { exists: { from: { source: LINES, alias: "l" }, where: { op: "eq", left: { column: "l.invoice_id" }, right: { column: "i.id" } } } },
        ],
      },
    };
    const late = {
      name: "late",
      from: { source: "early", alias: "e" },
      select: [{ name: "id", expression: { column: "e.id" } }],
    };
    const { found, q } = bind(
      query({
        with: [exists, late],
        from: { source: "late", alias: "x" },
        select: [{ name: "id", expression: { column: "x.id" } }],
        where: { op: "ne", left: { column: "x.id" }, right: { param: "other" } },
        parameters: [
          { name: "other", type: "uuid" },
          { name: "invoiceId", type: "uuid" },
        ],
      }),
    );
    expect(found).toEqual([]);
    const text = renderQuery(q, "postgresql", { placeholder: "$" });
    expect(text.parameters).toEqual(["invoiceId", "other"]);
    expect(text.sql).toBe(
      [
        "WITH early (id) AS (",
        "    SELECT i.id AS id",
        "    FROM billing.invoices i",
        "    WHERE i.id = $1 AND EXISTS (SELECT 1",
        "        FROM billing.invoice_lines l",
        "        WHERE l.invoice_id = i.id)",
        "),",
        "late (id) AS (",
        "    SELECT e.id AS id",
        "    FROM early e",
        ")",
        "SELECT x.id AS id",
        "FROM late x",
        "WHERE x.id <> $2",
      ].join("\n"),
    );
  });

  it("repeats in a collection's statement only the entries it reads, in list order", () => {
    const other = { name: "unused", from: { source: INVOICES, alias: "u" }, select: [{ name: "id", expression: { column: "u.id" } }] };
    const { found, result } = bind(
      query({
        with: [other, chain()],
        from: { source: INVOICES, alias: "i" },
        select: [{ name: "id", expression: { column: "i.id" } }],
        collections: [
          {
            attribute: "parents",
            query: {
              from: { source: "chain", alias: "k" },
              select: [{ name: "depth", expression: { column: "k.depth" } }],
              where: { op: "eq", left: { column: "k.id" }, right: { column: "i.id" } },
            },
          },
        ],
      }),
    );
    expect(found).toEqual([]);
    const view = result.queries.find((x) => x.id === QUERY)!;
    expect(view.collections[0].sql.startsWith("WITH RECURSIVE chain (id, depth) AS (")).toBe(true);
    expect(view.collections[0].sql).not.toContain("unused");
    // The top statement keeps every entry.
    expect(view.sql!.startsWith("WITH RECURSIVE unused (id) AS (")).toBe(true);
  });

  it("projects with, columns, recursive and union, and a source's namedQuery", () => {
    const { result } = bind(query());
    const view = result.queries.find((x) => x.id === QUERY)!;
    expect(view.with).toHaveLength(1);
    expect(view.with![0]).toMatchObject({ name: "chain", union: "all" });
    expect(view.with![0].columns.map((c) => c.name)).toEqual(["id", "depth"]);
    expect(view.with![0].query).not.toHaveProperty("recursive");
    expect(view.with![0].recursive).toBeDefined();
    expect(view.from).toMatchObject({ source: "chain", namedQuery: "chain", schema: null });
  });

  it("puts the WITH of a built query first in a binding's select over it", () => {
    const backend = new MockBackend();
    backend.model.create({ kind: "query", database: MAIN, ...query() });
    const sql = backend.generation.databaseView(MAIN)!.view!.queries!.find((x) => x.id === QUERY)!.sql!;
    const { with: head, statement } = splitWith(sql);
    expect(head.endsWith(")")).toBe(true);
    expect(head.startsWith("WITH RECURSIVE chain")).toBe(true);
    expect(statement).toBe("SELECT x.id AS id, x.depth AS depth\nFROM chain x");
    expect(splitWith("SELECT 1")).toEqual({ with: "", statement: "SELECT 1" });
  });
});

describe("hand-written queries under a binding", () => {
  const NOTE = "01K6BND0000000000000000010";
  const BINDING = "01K6BND0000000000000000015";
  const bound = (sql: string) => {
    const backend = new MockBackend();
    backend.model.create({
      kind: "query",
      database: MAIN,
      id: QUERY,
      name: "Q",
      sql: { "*": sql },
      columns: [{ name: "id", type: "uuid", nullable: false }],
      reads: [INVOICES],
    });
    const settings = backend.model.projectSettings();
    const docs = new Map(backend.model.docs());
    const entity = structuredClone(docs.get(NOTE)!) as Json;
    const binding = (entity.bindings as Json[]).find((b) => b.id === BINDING)!;
    binding.source = QUERY;
    binding.fields = [];
    binding.columns = [];
    delete binding.constants;
    docs.set(NOTE, entity as never);
    const input = {
      docs,
      conventions: settings.conventions as Record<string, unknown>,
      databaseConventions: settings.databases as Record<string, Record<string, unknown>>,
    };
    return bindingSql(input, NOTE, BINDING, null, "@")!.preview!.select!.sql;
  };

  it("nests the text whole: one final semicolon and the comments after it go, a leading WITH stays inside", () => {
    expect(bound("WITH a AS (SELECT id FROM billing.invoices) SELECT id FROM a; -- note\n")).toBe(
      "SELECT *\nFROM (\nWITH a AS (SELECT id FROM billing.invoices) SELECT id FROM a\n) q",
    );
    expect(bound("SELECT id FROM billing.invoices -- c")).toBe("SELECT *\nFROM (\nSELECT id FROM billing.invoices -- c\n) q");
  });
});

describe("named queries: rules", () => {
  const sentence = (rules: string[][], rule: string, pointer: string) => rules.find((r) => r[0] === rule && r[1] === pointer)?.[2];

  it("MQ4099: a named query reads one listed after it, or itself outside recursive", () => {
    const early = { name: "early", from: { source: "late", alias: "a" }, select: [{ name: "id", expression: { column: "a.id" } }] };
    const late = { name: "late", from: { source: INVOICES, alias: "i" }, select: [{ name: "id", expression: { column: "i.id" } }] };
    const first = bind(query({ with: [early, late], from: { source: "late", alias: "x" }, select: [{ name: "id", expression: { column: "x.id" } }] }));
    expect(sentence(first.rules, "MQ4099", "/with/0/from/source")).toBe(
      "Named query 'early' of query 'Q' reads named query 'late', which is listed after it: move 'late' before it.",
    );
    expect(first.q.sql).toBe("");
    const self = { name: "self", from: { source: "self", alias: "s" }, select: [{ name: "id", expression: { value: 1 } }] };
    const second = bind(query({ with: [self], from: { source: "self", alias: "x" }, select: [{ name: "id", expression: { column: "x.id" } }] }));
    expect(sentence(second.rules, "MQ4099", "/with/0/from/source")).toBe(
      "Named query 'self' of query 'Q' reads itself outside recursive: only its repeating part may read its own name.",
    );
    // A join's source points at the join.
    const joined = {
      name: "j",
      from: { source: INVOICES, alias: "i" },
      joins: [{ source: "later", alias: "l", on: { op: "eq", left: { column: "i.id" }, right: { column: "l.id" } } }],
      select: [{ name: "id", expression: { column: "i.id" } }],
    };
    const later = { ...late, name: "later" };
    expect(
      bind(query({ with: [joined, later], from: { source: "later", alias: "x" }, select: [{ name: "id", expression: { column: "x.id" } }] })).rules.map(
        (r) => r[1],
      ),
    ).toContain("/with/0/joins/0/source");
  });

  it("MQ4099: a named query has the name of a table the query reads", () => {
    const entry = { name: "Invoices", from: { source: INVOICES, alias: "i" }, select: [{ name: "id", expression: { column: "i.id" } }] };
    const { rules } = bind(query({ with: [entry], from: { source: "Invoices", alias: "x" }, select: [{ name: "id", expression: { column: "x.id" } }] }));
    expect(sentence(rules, "MQ4099", "/with/0/name")).toBe(
      "Named query 'Invoices' of query 'Q' has the name of table 'invoices', which the query reads, so the SQL would read one for the other: rename the named query.",
    );
  });

  it("MQ3001 for a repeated name, MQ4021 for an unknown source, MQ4023 for a column the named query lacks", () => {
    const a = { name: "late", from: { source: INVOICES, alias: "i" }, select: [{ name: "id", expression: { column: "i.id" } }] };
    const dup = bind(
      query({ with: [a, { ...a, name: "LATE" }], from: { source: "late", alias: "x" }, select: [{ name: "id", expression: { column: "x.id" } }] }),
    );
    expect(sentence(dup.rules, "MQ3001", "/with/1/name")).toBe("Named query name 'LATE' is already used in query 'Q' (ignoring case).");
    // MQ3001 keeps the query's SQL.
    const exact = bind(query({ with: [a, a], from: { source: INVOICES, alias: "x" }, select: [{ name: "id", expression: { column: "x.id" } }] }));
    expect(sentence(exact.rules, "MQ3001", "/with/1/name")).toBe("Named query name 'late' is already used in query 'Q'.");
    const nowhere = bind(query({ with: [a], from: { source: "nowhere", alias: "x" }, select: [{ name: "id", expression: { value: 1 } }] }));
    expect(sentence(nowhere.rules, "MQ4021", "/from/source")).toBe(
      "Query 'Q' reads 'nowhere', which is no table or view of database 'main' and no named query of the query.",
    );
    expect(
      bind({ ...query(), with: undefined, from: { source: "nowhere", alias: "x" }, select: [{ name: "id", expression: { value: 1 } }] }).rules.find(
        (r) => r[0] === "MQ4021",
      )?.[2],
    ).toBe("Query 'Q' reads 'nowhere', which is no table or view of database 'main'.");
    const missing = bind(query({ with: [a], from: { source: "late", alias: "x" }, select: [{ name: "id", expression: { column: "x.missing" } }] }));
    expect(sentence(missing.rules, "MQ4023", "/select/0/expression/column")).toBe(
      "Query 'Q' names column 'missing' of 'x' (named query 'late'), which has no such column.",
    );
  });

  it("MQ4099: a repeating part that does not fit its anchor", () => {
    const part = chain().recursive as Json;
    const at = (patch: Json, dialect?: string) => bind(query({ with: [chain({ recursive: { ...part, ...patch } })] }), dialect).rules;
    const select = part.select as Json[];
    expect(sentence(at({ select: [select[0]] }), "MQ4099", "/with/0/recursive/select")).toBe(
      "The repeating part of named query 'chain' of query 'Q' selects (id) where its anchor selects (id, depth): give it the same fields, with the same names in the same order.",
    );
    expect(sentence(at({ select: [select[1], select[0]] }), "MQ4099", "/with/0/recursive/select")).toContain("selects (depth, id)");
    expect(sentence(at({ select: [select[0], { name: "depth", expression: { column: "p.number" } }] }), "MQ4099", "/with/0/recursive/select/1")).toBe(
      "Field 'depth' of the repeating part of named query 'chain' of query 'Q' is of type 'string', and the anchor's of type 'int32': give both the same type (a cast).",
    );
    expect(sentence(at({ from: { source: INVOICES, alias: "ch" }, joins: [] }), "MQ4099", "/with/0/recursive/from")).toBe(
      "The repeating part of named query 'chain' of query 'Q' does not read 'chain': a repeating part reads its own name exactly once, in its from or a join.",
    );
    const twice = at({ joins: [{ source: "chain", alias: "d", on: { op: "eq", left: { column: "d.id" }, right: { column: "ch.id" } } }] });
    expect(sentence(twice, "MQ4099", "/with/0/recursive/joins/0/source")).toBe(
      "The repeating part of named query 'chain' of query 'Q' reads 'chain' 2 times: a repeating part reads its own name exactly once.",
    );
    const outer = at({
      from: { source: INVOICES, alias: "p" },
      joins: [{ source: "chain", alias: "ch", kind: "left", on: { op: "eq", left: { column: "p.id" }, right: { column: "ch.id" } } }],
    });
    expect(sentence(outer, "MQ4099", "/with/0/recursive/joins/0/source")).toBe(
      "The repeating part of named query 'chain' of query 'Q' reads 'chain' on the null-padded side of an outer join (the right side of a left join, the left side of a right join, either side of a full join), which the databases refuse: read it on the side that keeps its rows, or use an inner join.",
    );
    const nested = at({ from: { source: INVOICES, alias: "p" }, joins: [], where: { exists: { from: { source: "chain", alias: "z" } } } });
    expect(sentence(nested, "MQ4099", "/with/0/recursive/where/exists/from/source")).toBe(
      "The repeating part of named query 'chain' of query 'Q' reads 'chain' in a nested query: a repeating part reads its own name in its own from or a join.",
    );
    const aggregate = at({ select: [select[0], { name: "depth", expression: { call: "count" } }] });
    expect(sentence(aggregate, "MQ4099", "/with/0/recursive/select")).toBe(
      "The repeating part of named query 'chain' of query 'Q' calls an aggregate function, which a repeating part may not.",
    );
  });

  it("MQ4099: SQL Server and Oracle take no union distinct; SQL Server no outer join or nested query in the repeating part", () => {
    const part = chain().recursive as Json;
    const distinct = bind(query({ with: [chain({ recursive: { ...part, union: "distinct" } })] }), "sqlserver").rules;
    expect(sentence(distinct, "MQ4099", "/with/0/recursive/union")).toBe(
      "The repeating part of named query 'chain' of query 'Q' adds its rows with union distinct, which SQL Server (database 'main') does not take in a recursive query: use union all.",
    );
    expect(
      bind(query({ with: [chain({ recursive: { ...part, union: "distinct" } })] }), "oracle").rules.some(
        (r) => r[0] === "MQ4099" && r[2].includes("Oracle (database 'main')"),
      ),
    ).toBe(true);
    expect(bind(query({ with: [chain({ recursive: { ...part, union: "distinct" } })] })).rules.filter((r) => r[0] === "MQ4099")).toEqual([]);
    const join = (part.joins as Json[])[0];
    const outer = bind(query({ with: [chain({ recursive: { ...part, joins: [{ ...join, kind: "left" }] } })] }), "sqlserver").rules;
    expect(sentence(outer, "MQ4099", "/with/0/recursive/joins/0/kind")).toBe(
      "The repeating part of named query 'chain' of query 'Q' has an outer join, which SQL Server (database 'main') does not take in a repeating part: use an inner join.",
    );
    const exists = { exists: { from: { source: INVOICES, alias: "z" } } };
    const nested = bind(query({ with: [chain({ recursive: { ...part, where: exists } })] }), "sqlserver").rules;
    expect(sentence(nested, "MQ4099", "/with/0/recursive")).toBe(
      "The repeating part of named query 'chain' of query 'Q' has a nested query (exists), which SQL Server (database 'main') does not take in a repeating part.",
    );
  });

  it("merges the repeating part's nullability and fills an unknown anchor type from it", () => {
    const part = chain().recursive as Json;
    const { result } = bind(
      query({ with: [chain({ recursive: { ...part, select: [(part.select as Json[])[0], { name: "depth", nullable: true, expression: { value: 1 } }] } })] }),
    );
    expect(result.queries.find((x) => x.id === QUERY)!.with![0].columns.map((c) => [c.name, c.nullable])).toEqual([
      ["id", false],
      ["depth", true],
    ]);
  });
});

describe("named queries: bindings and the editor", () => {
  it("MQ4044 for a binding over a hand-written query that writes, with the engine's text; MQ4097 only on SQL Server", () => {
    const names = { entity: "Invoice", database: "main" };
    const source = (extra: Json, dialect = "postgresql", texts: Json = { "*": "SELECT 1" }) =>
      sourceOf(
        {
          dialect,
          tables: [],
          views: [],
          queries: [{ id: "Q", name: "X", form: "sql", select: [{ name: "a", type: "int32", nullable: false }], texts, ...extra }],
        } as never,
        "Q",
      );
    const writes = bindingProblems({ source: "Q", fields: [] } as never, [], source({ writes: ["T"] }), names);
    expect(writes.map((p) => [p.rule, p.pointer, p.message])).toEqual([
      [
        "MQ4044",
        "/source",
        "The binding of entity 'Invoice' to database 'main' reads query 'X', which is written as SQL text and writes tables: an entity reads only through a query that returns rows and changes none.",
      ],
    ]);
    expect(bindingProblems({ source: "Q", fields: [] } as never, [], source({ select: [] }), names)[0].message).toContain("and declares no columns:");
    const ordered = source({}, "sqlserver", { "*": "SELECT a FROM t ORDER BY a;" });
    expect(bindingProblems({ source: "Q", fields: [] } as never, [], ordered, names).map((p) => [p.rule, p.severity])).toContainEqual(["MQ4097", "warning"]);
    expect(
      bindingProblems({ source: "Q", fields: [] } as never, [], source({}, "sqlserver", { "*": "WITH a AS (SELECT 1) SELECT * FROM a" }), names)[0].message,
    ).toContain("whose SQL text starts with WITH,");
    expect(
      bindingProblems({ source: "Q", fields: [] } as never, [], source({}, "postgresql", { "*": "SELECT a FROM t ORDER BY a" }), names).map((p) => p.rule),
    ).not.toContain("MQ4097");
  });

  const doc = () =>
    structuredClone(
      query({
        from: { source: "chain", alias: "chain" },
        select: [
          { name: "id", expression: { column: "chain.id" } },
          { name: "depth", expression: { column: "chain.depth" } },
        ],
      }),
    ) as unknown as QueryDoc;

  it("renames a named query across sources, aliases and column references in one edit", () => {
    const d = doc();
    expect(renameNamedQuery(d, "chain", "ancestors")).toBe(true);
    expect(d.with![0].name).toBe("ancestors");
    expect(d.with![0].recursive!.from.source).toBe("ancestors");
    expect(d.from).toEqual({ source: "ancestors", alias: "ancestors" });
    expect(d.select.map((f) => (f.expression as { column: string }).column)).toEqual(["ancestors.id", "ancestors.depth"]);
    // The repeating part kept its alias; its references did not change.
    expect(d.with![0].recursive!.from.alias).toBe("ch");
    expect(JSON.stringify(d.with![0].recursive!.select)).toContain("ch.depth");
    expect(bind({ ...(d as unknown as Json) }).found).toEqual([]);
    const second = doc();
    second.with!.push({ ...(structuredClone(chain({ name: "other" })) as unknown as NamedQuery), recursive: undefined });
    expect(renameNamedQuery(second, "chain", "other")).toBe(false);
  });

  it("renames a named query's field and the references through every source that reads it", () => {
    const d = doc();
    expect(renameNamedField(d, "chain", 1, "level")).toBe(true);
    expect(d.with![0].select[1].name).toBe("level");
    expect(d.with![0].recursive!.select[1].name).toBe("level");
    expect(d.select.map((f) => (f.expression as { column: string }).column)).toEqual(["chain.id", "chain.level"]);
    // The repeating part's own reads of its name follow.
    expect(JSON.stringify(d.with![0].recursive!.select)).toContain("ch.level");
    const { found } = bind({
      ...(d as unknown as Json),
      select: [
        { name: "id", expression: { column: "chain.id" } },
        { name: "level", expression: { column: "chain.level" } },
      ],
    });
    expect(found).toEqual([]);
    expect(renameNamedField(d, "chain", 0, "level")).toBe(false);
  });

  it("renames a field through the alias in collections, but not a column of another source that reuses the alias in an exists", () => {
    const d = doc();
    d.collections = [
      {
        attribute: "Lines",
        query: {
          from: { source: LINES, alias: "l" },
          select: [{ name: "id", expression: { column: "l.id" } }],
          where: { op: "eq", left: { column: "l.invoice" }, right: { column: "chain.depth" } },
        },
      },
    ] as never;
    d.where = {
      exists: {
        from: { source: LINES, alias: "chain" },
        select: [{ name: "x", expression: { column: "chain.depth" } }],
        where: { op: "eq", left: { column: "chain.depth" }, right: { value: 1 } },
      },
    } as never;
    expect(renameNamedField(d, "chain", 1, "level")).toBe(true);
    const text = JSON.stringify(d);
    expect(text).toContain('"right":{"column":"chain.level"}');
    expect(JSON.stringify((d.collections as unknown as { query: unknown }[])[0])).toContain("chain.level");
    expect(JSON.stringify(d.where)).not.toContain("chain.level");
    expect(JSON.stringify(d.where)).toContain("chain.depth");
  });

  it("lists named queries as sources with their columns, scoped to the first entries", () => {
    const d = doc();
    const options = namedOptions(d.with, 1, [
      {
        name: "chain",
        columns: [
          { name: "id", type: "uuid" },
          { name: "depth", type: "int32" },
        ],
      },
    ]);
    expect(options).toHaveLength(1);
    expect(options[0]).toMatchObject({
      value: "chain",
      kind: "named",
      namedColumns: [
        { name: "id", type: "uuid" },
        { name: "depth", type: "int32" },
      ],
    });
    expect(namedOptions(d.with, 0)).toEqual([]);
    const scope = scopeOf(d, undefined, [], [], options);
    expect(scope[0]).toMatchObject({ alias: "chain" });
    expect(scope[0].columns.map((c) => c.name)).toEqual(["id", "depth"]);
  });
});
