// The gate 2 walk (SPEC Section 21, phase2-design.md section 7.2 step 2), project live only, against the image started over
// the seeded 200-entity reference application (samples/reference-app/tools/gate2.sh walk, .github/workflows/gate2.yml).
// Open the editor, find Warehouse in the Domain model tree and open its editor, add an attribute through the grid, rename a
// relation in its relationship editor, then plan all packs from the Generate explorer, open a diff and apply; the apply
// job's applyResult.outcome must be "succeeded" (its state is "succeeded" for any apply that ran to completion, stale or
// conflicting ones included, so it is not what counts).
// Skipped unless MAQUETTISTE_GATE2 is set: the mock and plain live runs serve the billing fixture, not this model.
import type { Locator, Page } from "@playwright/test";
import { expect, test, workspace } from "./fixtures";

test.skip(!process.env.MAQUETTISTE_GATE2, "gate 2 only: set MAQUETTISTE_GATE2 and serve the seeded reference application");

const slow = { timeout: 120_000 };
test.use({ actionTimeout: 30_000 });
// No retry: the walk edits the model, so a second attempt would start from a changed project (rerun gate2.sh from prepare).
test.describe.configure({ retries: 0 });

/** Same-origin fetch from the page: Node may not resolve *.localhost, the browser always does (so no page.request). */
async function api(page: Page, method: string, path: string, body?: unknown): Promise<{ status: number; json: unknown }> {
  return page.evaluate(
    async ([m, p, b]) => {
      const res = await fetch(p as string, {
        method: m as string,
        headers: b === undefined ? {} : { "Content-Type": "application/json" },
        body: b === undefined ? undefined : JSON.stringify(b),
      });
      const text = await res.text();
      return { status: res.status, json: text ? (JSON.parse(text) as unknown) : null };
    },
    [method, path, body] as const,
  );
}

/** Signs the browser in with the editor token (the container may see the browser as a remote peer, not a local one). */
async function signIn(page: Page): Promise<void> {
  const token = process.env.MAQUETTISTE_EDITOR_TOKEN;
  if (!token) return;
  await page.goto("/healthz");
  const res = await api(page, "POST", "/api/session", { token });
  expect(res.status, "POST /api/session").toBe(200);
}

/** The Domain model explorer: the sidebar under the icon rail, one explorer at a time. */
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

/** Searches the Domain model explorer for an exact element name and opens that element in a pinned editor tab. */
async function openInEditor(page: Page, name: string): Promise<Locator> {
  const side = explorer(page);
  const search = side.getByLabel("Search the model");
  await search.fill(name);
  const row = side.getByTestId(`explorer-row-${name}`);
  // One double click on the unselected row selects it and opens the pinned editor.
  await row.dblclick();
  const editor = page.getByRole("region", { name: `Editor: ${name}` });
  await expect(editor).toBeVisible(slow);
  await search.fill("");
  return editor;
}

test("gate 2: edit the reference application in the editor, plan and apply", async ({ page }) => {
  test.setTimeout(600_000);
  await signIn(page);
  await page.goto("/");
  await expect(page.getByTestId("shell")).toBeVisible(slow);
  // The seeded model, not the billing fixture: the Domain model tree lists its domains, collapsed, with their entity counts.
  const tree = explorer(page).getByRole("tree", { name: "Domain model" });
  await expect(tree.getByRole("treeitem", { name: /^Billing and payments \d+ entities/ })).toBeVisible(slow);
  await expect(tree.getByRole("treeitem", { name: /^Inventory and warehousing \d+ entities/ })).toBeVisible();

  // Open Warehouse in the entity editor and add an attribute through its grid.
  const warehouse = await openInEditor(page, "Warehouse");
  await warehouse.getByRole("tab", { name: "Attributes" }).click();
  const grid = warehouse.getByTestId("attribute-grid");
  const names = grid.locator('td[data-column="name"]');
  const before = await names.count();
  expect(before).toBeGreaterThan(3);
  await warehouse.getByRole("button", { name: "Add attribute" }).click();
  await expect(names).toHaveCount(before + 1);
  // The new row opens in edit mode: its name editor has focus with the generated attribute<n> selected, so typing
  // replaces it.
  const editor = names.nth(before).getByRole("textbox");
  await expect(editor).toBeVisible();
  await expect(editor).toBeFocused();
  await expect(editor).toHaveValue(/^attribute\d+$/);
  await page.keyboard.type("receivingHours");
  await expect(editor).toHaveValue("receivingHours");
  await editor.press("Enter");
  await expect(names).toHaveCount(before + 1);
  await expect(names.nth(before)).toHaveText("receivingHours");
  await expect(page.getByTestId("save-status")).toHaveText("Saved", slow);

  // Rename a relation in the relationship editor: the name field saves on blur, and the tab follows the new name.
  const relation = await openInEditor(page, "delivery route departs from warehouse");
  const name = relation.getByLabel("Name", { exact: true });
  await expect(name).toHaveValue("delivery route departs from warehouse");
  await name.fill("delivery route departs from depot");
  // The region's name follows the draft as it is typed, so the locator above no longer matches: Tab from the focus.
  await page.keyboard.press("Tab");
  await expect(page.getByRole("region", { name: "Editor: delivery route departs from depot" })).toBeVisible(slow);
  await expect(page.getByTestId("save-status")).toHaveText("Saved", slow);
  const search = explorer(page).getByLabel("Search the model");
  await search.fill("delivery route");
  await expect(explorer(page).getByTestId("explorer-row-delivery route departs from depot")).toBeVisible();
  await expect(explorer(page).getByTestId("explorer-row-delivery route departs from warehouse")).toHaveCount(0);
  await search.fill("");
  await expect(page.getByRole("tab", { name: /Problems/ })).not.toContainText(/[1-9]/, slow);

  // Generate: plan every pack, read a diff.
  await workspace(page, "Generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-result")).toContainText("Plan ready", slow);
  const summary = page.getByTestId("plan-summary");
  await expect(summary).toContainText("sql-ddl:");
  await expect(summary).toContainText("csharp-dapper:");
  const rows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  expect(await rows.count()).toBeGreaterThan(0);
  // A file to write has a diff (the plan also lists files identical to the disk, which have none).
  await page.getByTestId("plan-filter-write").click();
  await page.locator("#filter-pack").selectOption("sql-ddl");
  const first = rows.first();
  const path = (await first.getByRole("gridcell").nth(1).innerText()).trim();
  await first.click();
  const diff = page.getByTestId("diff-viewer");
  await expect(diff).toContainText(path, slow);
  await expect(diff).toContainText("@@");
  await page.locator("#filter-pack").selectOption("");
  await page.getByTestId("plan-filter-all").click();

  // Apply by plan id; the job's result (after job.completed) must be a succeeded apply.
  const started = page.waitForResponse((r) => r.url().endsWith("/api/generate/apply") && r.request().method() === "POST");
  await page.getByTestId("apply").click();
  const response = await started;
  expect(response.status(), "POST /api/generate/apply").toBe(202);
  const job = (await response.json()) as { id: string };
  const result = page.getByTestId("apply-result");
  await expect(result).toBeVisible({ timeout: 480_000 });
  await expect(result.getByTestId("outcome")).toHaveText(/succeeded/);
  await expect(result).toContainText(/[1-9]\d* written/);

  const final = await api(page, "GET", `/api/jobs/${job.id}`);
  expect(final.status).toBe(200);
  const info = final.json as { state: string; applyResult?: { outcome?: string; result?: { filesWritten?: number } } };
  expect(info.applyResult?.outcome, "applyResult.outcome").toBe("succeeded");
  expect(info.applyResult?.result?.filesWritten ?? 0).toBeGreaterThan(800);
});
