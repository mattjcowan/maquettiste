// The Storage tab of a relation with several types, on the mock (several-types-design.md 19.10): the types left out and the gaps the database
// can't enforce with the text to copy, a gap's Copy button, and the quick fix of the finding in the Problems panel.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const ulid = (n: number) => `01J92P0VZZ${String(n).padStart(16, "0")}`;
const ID = {
  db: ulid(1),
  comment: ulid(2),
  part: ulid(3),
  assembly: ulid(4),
  tool: ulid(5),
  rel: ulid(6),
  mapping: ulid(7),
  tComments: ulid(8),
  tParts: ulid(9),
  tAssemblies: ulid(10),
  col: (n: number) => ulid(20 + n),
  att: (n: number) => ulid(40 + n),
  end: (n: number) => ulid(60 + n),
  map: (n: number) => ulid(80 + n),
};

const entity = (id: string, name: string, n: number) => ({
  kind: "entity",
  id,
  name,
  package: BILLING,
  key: { attributes: [ID.att(n)], strategy: "uuid-v7" },
  attributes: [{ id: ID.att(n), name: "id", type: "uuid", required: true }],
});
const table = (id: string, name: string, columns: [string, string, string?][]) => ({
  kind: "table",
  id,
  name,
  database: ID.db,
  columns: columns.map(([cid, cname, type]) => ({ id: cid, name: cname, type: type ?? "uuid", ...(type === "string" ? { length: 20 } : {}) })),
  primaryKey: { columns: [columns[0][0]] },
});

async function seed(page: Page) {
  const operations = [
    { kind: "database", id: ID.db, name: "Store", dialect: "postgresql", byConvention: "none" },
    entity(ID.comment, "Comment", 1),
    entity(ID.part, "Part", 2),
    entity(ID.assembly, "Assembly", 3),
    // Tool is covered but has no table in the database: it is left out.
    entity(ID.tool, "Tool", 4),
    table(ID.tComments, "comments", [
      [ID.col(1), "id"],
      [ID.col(2), "attached_type", "string"],
      [ID.col(3), "attached_id"],
    ]),
    table(ID.tParts, "parts", [[ID.col(4), "id"]]),
    table(ID.tAssemblies, "assemblies", [[ID.col(5), "id"]]),
    { kind: "mapping", id: ID.map(1), name: "Comment in Store", database: ID.db, entity: ID.comment, table: ID.tComments },
    { kind: "mapping", id: ID.map(2), name: "Part in Store", database: ID.db, entity: ID.part, table: ID.tParts },
    { kind: "mapping", id: ID.map(3), name: "Assembly in Store", database: ID.db, entity: ID.assembly, table: ID.tAssemblies },
    { kind: "mapping", id: ID.map(4), name: "Tool in Store", database: ID.db, entity: ID.tool, ignore: true },
    {
      kind: "relation",
      id: ID.rel,
      name: "AttachedTo",
      package: BILLING,
      ends: [
        { id: ID.end(1), entity: ID.comment, role: "comment", navigation: "attachedTos" },
        { id: ID.end(2), role: "attachedTo", navigation: "target", max: 1, types: [{ entity: ID.part }, { entity: ID.assembly }, { entity: ID.tool }] },
      ],
    },
    {
      kind: "mapping",
      id: ID.mapping,
      name: "AttachedTo in Store",
      database: ID.db,
      relation: ID.rel,
      ends: [{ end: ID.end(2), typeColumn: ID.col(2), idColumn: ID.col(3) }],
    },
  ].map((element) => ({ op: "create", element }));
  const outcome = await page.evaluate(
    async (body) => {
      const res = await fetch("/api/model/batch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      return (await res.json()) as { outcome: string; diagnostics?: { rule: string; message: string }[] };
    },
    { operations },
  );
  expect(outcome.outcome, JSON.stringify(outcome.diagnostics)).toBe("saved");
}

test("the Storage tab lists what is left out and what the database can't enforce, with the text to copy", async ({ page }) => {
  await openEditor(page);
  await seed(page);
  await page.getByLabel("Search the model").fill("AttachedTo");
  await page.getByTestId("explorer-row-AttachedTo").dblclick();
  const editor = page.getByRole("region", { name: "Editor: AttachedTo" });
  await editor.getByRole("tab", { name: "Storage" }).click();
  const store = editor.getByTestId("relation-storage-database").filter({ hasText: "Store" });

  await expect(store.getByTestId("relation-storage-left-out")).toHaveText("Left out (not in this database): Tool");
  const gaps = store.getByTestId("relation-storage-gap");
  await expect(gaps).toHaveCount(2);
  await expect(gaps.nth(0)).toHaveAttribute("data-kind", "no-foreign-key");
  await expect(gaps.nth(1)).toHaveAttribute("data-kind", "type-check");
  await expect(store.getByTestId("relation-storage-gap-text")).toHaveText("CONSTRAINT ck_comments_attached_to CHECK (attached_type IN ('Assembly', 'Part'))");

  // Copy is an icon button with a tooltip; it says so once it has copied.
  const copy = store.getByTestId("relation-storage-gap-copy");
  await expect(copy).toHaveAttribute("title", "Copy the text");
  await copy.click();
  await expect(copy).toHaveAttribute("title", "Copied");
});

test("the finding about a gap offers Copy the check in the Problems panel", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await openEditor(page);
  await seed(page);
  await page.getByRole("tab", { name: /Problems/ }).click();
  const problems = page.getByTestId("problems-list");
  const fix = problems.getByTestId("problem-fix-MQ4118");
  await expect(fix).toHaveText("Copy the check");
  await fix.click();
  await expect
    .poll(() => page.evaluate(() => navigator.clipboard.readText()))
    .toBe("CONSTRAINT ck_comments_attached_to CHECK (attached_type IN ('Assembly', 'Part'))");
});

// ---------------------------------------------------------------------------------------------------------------------------------
// Picking on a mapping that already exists, setting up A type and an id, and choosing a link table before the entries exist.

const M = {
  memo: ulid(100),
  tMemos: ulid(101),
  tLinks: ulid(102),
  relMemo: ulid(103),
  relPinned: ulid(104),
  relTagged: ulid(105),
  mapMemo: ulid(106),
  mapEntity: ulid(107),
  fkPart: ulid(120),
  fkAssembly: ulid(121),
  fkLink: ulid(122),
  col: (n: number) => ulid(110 + n),
};

async function seedPicking(page: Page) {
  await seed(page);
  const memos = table(M.tMemos, "memos", [
    [M.col(1), "id"],
    [M.col(2), "part_id"],
    [M.col(3), "assembly_id"],
    [M.col(4), "pin_type", "string"],
    [M.col(5), "pin_id"],
  ]);
  const links = table(M.tLinks, "memo_links", [
    [M.col(6), "id"],
    [M.col(7), "memo_id"],
    [M.col(8), "subject_type", "string"],
    [M.col(9), "subject_id"],
  ]);
  const attachedTypes = [{ entity: ID.part }, { entity: ID.assembly }];
  const operations = [
    entity(M.memo, "Memo", 9),
    {
      ...memos,
      foreignKeys: [
        { id: M.fkPart, name: "fk_part", columns: [M.col(2)], referencesTable: ID.tParts },
        { id: M.fkAssembly, name: "fk_assembly", columns: [M.col(3)], referencesTable: ID.tAssemblies },
      ],
    },
    { ...links, foreignKeys: [{ id: M.fkLink, name: "fk_memo", columns: [M.col(7)], referencesTable: M.tMemos }] },
    { kind: "mapping", id: M.mapEntity, name: "Memo in Store", database: ID.db, entity: M.memo, table: M.tMemos },
    {
      kind: "relation",
      id: M.relMemo,
      name: "Subject",
      package: BILLING,
      ends: [
        { id: ID.end(10), entity: M.memo, role: "memo", navigation: "subjects" },
        { id: ID.end(11), role: "subject", navigation: "target", max: 1, types: attachedTypes },
      ],
    },
    {
      kind: "mapping",
      id: M.mapMemo,
      name: "Subject in Store",
      database: ID.db,
      relation: M.relMemo,
      ends: [{ end: ID.end(11), types: [{ entity: ID.part, foreignKey: M.fkPart }] }],
    },
    {
      kind: "relation",
      id: M.relPinned,
      name: "Pinned",
      package: BILLING,
      ends: [
        { id: ID.end(12), entity: M.memo, role: "memo", navigation: "pins" },
        { id: ID.end(13), role: "pinnedTo", navigation: "pinned", max: 1, types: attachedTypes },
      ],
    },
    {
      kind: "relation",
      id: M.relTagged,
      name: "Tagged",
      package: BILLING,
      ends: [
        { id: ID.end(14), entity: M.memo, role: "memo", navigation: "tags", max: "*" },
        { id: ID.end(15), role: "tagged", navigation: "tagged", max: "*", types: attachedTypes },
      ],
    },
  ].map((element) => ({ op: "create", element }));
  const outcome = await page.evaluate(
    async (body) => {
      const res = await fetch("/api/model/batch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      return (await res.json()) as { outcome: string; diagnostics?: { rule: string; message: string }[] };
    },
    { operations },
  );
  expect(outcome.outcome, JSON.stringify(outcome.diagnostics)).toBe("saved");
}

async function openStorage(page: Page, name: string) {
  await page.getByLabel("Search the model").fill(name);
  await page.getByTestId(`explorer-row-${name}`).dblclick();
  const editor = page.getByRole("region", { name: `Editor: ${name}` });
  await editor.getByRole("tab", { name: "Storage" }).click();
  return editor.locator(`[data-testid="relation-storage-database"][data-database="Store"]`);
}

type Row = { id: string; kind: string; entity?: string };

/** The relation mapping documents of one relation (a relation mapping names no entity). */
async function relationMappings(page: Page, relation: string) {
  return page.evaluate(async (rel) => {
    const index = (await (await fetch("/api/model/index")).json()) as Row[] | { elements: Row[] };
    const rows = (Array.isArray(index) ? index : index.elements).filter((r) => r.kind === "mapping" && !r.entity);
    const out: Record<string, unknown>[] = [];
    for (const row of rows) {
      const json = ((await (await fetch(`/api/model/elements/${row.id}`)).json()) as { json: Record<string, unknown> }).json;
      if (json.relation === rel) out.push(json);
    }
    return out;
  }, relation);
}

test("a key can be picked on a mapping that already exists", async ({ page }) => {
  await openEditor(page);
  await seedPicking(page);
  const store = await openStorage(page, "Subject");
  await expect(store.getByTestId("relation-held-in")).toHaveValue("other-table");
  const assembly = store.getByTestId("relation-type-key").nth(1);
  await assembly.selectOption(M.fkAssembly);
  await expect(assembly).toHaveValue(M.fkAssembly);
  await expect.poll(async () => ((await relationMappings(page, M.relMemo))[0]?.ends as { types: unknown[] }[])[0].types.length).toBe(2);
  const [mapping] = await relationMappings(page, M.relMemo);
  expect(mapping.ends).toEqual([
    {
      end: ID.end(11),
      types: [
        { entity: ID.part, foreignKey: M.fkPart },
        { entity: ID.assembly, foreignKey: M.fkAssembly },
      ],
    },
  ]);
});

test("A type and an id can be set up: the id column first, then the type column", async ({ page }) => {
  await openEditor(page);
  await seedPicking(page);
  const store = await openStorage(page, "Pinned");
  await store.getByTestId("relation-held-in").selectOption("other-table");
  await store.getByTestId("relation-linked-by").selectOption("type-and-id");
  await store.getByTestId("relation-id-column").selectOption(M.col(5));
  // The choice stays A type and an id, and the Type column picker is there to use.
  await expect(store.getByTestId("relation-linked-by")).toHaveValue("type-and-id");
  const typeColumn = store.getByTestId("relation-type-column");
  await expect(typeColumn).toBeEnabled();
  await typeColumn.selectOption(M.col(4));
  await expect(typeColumn).toHaveValue(M.col(4));
  await expect.poll(async () => (await relationMappings(page, M.relPinned))[0]?.ends).toEqual([{ end: ID.end(13), idColumn: M.col(5), typeColumn: M.col(4) }]);
});

test("A link table can be chosen first on a relation with no mapping; it is saved with its entries", async ({ page }) => {
  await openEditor(page);
  await seedPicking(page);
  const store = await openStorage(page, "Tagged");
  await store.getByTestId("relation-held-in").selectOption("link-table");
  await store.getByTestId("relation-link-table").selectOption(M.tLinks);
  // Held on the card, nothing saved: the link table needs a way for each end.
  await expect(store.getByTestId("relation-storage-held")).toBeVisible();
  await expect(store.getByTestId("relation-link-table")).toHaveValue(M.tLinks);
  expect(await relationMappings(page, M.relTagged)).toEqual([]);
  await store.getByTestId("relation-linked-by").nth(1).selectOption("type-and-id");
  await store.getByTestId("relation-id-column").selectOption(M.col(9));
  await store.getByTestId("relation-type-column").selectOption(M.col(8));
  await store.getByTestId("relation-entry-key").selectOption(M.fkLink);
  await expect(store.getByTestId("relation-storage-held")).toHaveCount(0);
  await expect.poll(async () => (await relationMappings(page, M.relTagged)).length).toBe(1);
  const [mapping] = await relationMappings(page, M.relTagged);
  expect(mapping).toMatchObject({ junctionTable: M.tLinks });
  expect((mapping.ends as { end: string }[]).map((e) => e.end).sort()).toEqual([ID.end(14), ID.end(15)].sort());
});
