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
