// The gate 2 walk (SPEC Section 21, phase2-design.md section 7.2 step 2), project live only, against the image started over
// the seeded 200-entity reference application (samples/reference-app/tools/gate2.sh walk, .github/workflows/gate2.yml).
// Open the editor, open Warehouse from the explorer, add an attribute through the grid, rename a relation in the inspector,
// then plan all packs in Generate, open a diff and apply; the apply job's applyResult.outcome must be "succeeded" (its state
// is "succeeded" for any apply that ran to completion, stale or conflicting ones included, so it is not what counts).
// Skipped unless MAQUETTISTE_GATE2 is set: the mock and plain live runs serve the billing fixture, not this model.
import type { Page } from "@playwright/test";
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

/** Filters the explorer on an exact element name and opens that element in the inspector. */
async function openFromExplorer(page: Page, name: string): Promise<void> {
  const filter = page.getByLabel("Filter elements by name");
  await filter.fill(name);
  await page.getByTestId(`explorer-row-${name}`).click();
  await expect(page.getByRole("region", { name: `Inspector: ${name}` })).toBeVisible();
  await filter.fill("");
}

test("gate 2: edit the reference application in the editor, plan and apply", async ({ page }) => {
  test.setTimeout(600_000);
  await signIn(page);
  await page.goto("/");
  await expect(page.getByTestId("shell")).toBeVisible(slow);
  // The seeded model, not the billing fixture: the explorer groups the elements by package (Billing alone holds 70).
  await expect(page.getByRole("listbox", { name: "Model elements" }).getByRole("option", { name: /^Billing \d+ (expanded|collapsed)$/ })).toBeVisible(slow);

  // Entities workspace: open Warehouse and add an attribute through the grid.
  await openFromExplorer(page, "Warehouse");
  const grid = page.getByTestId("attribute-grid");
  const names = grid.locator('td[data-column="name"]');
  const before = await names.count();
  expect(before).toBeGreaterThan(3);
  await page.getByRole("button", { name: "Add attribute" }).click();
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

  // Rename a relation in the inspector: the name field saves on blur.
  await openFromExplorer(page, "delivery route warehouse");
  const inspector = page.getByRole("region", { name: "Inspector: delivery route warehouse" });
  const name = inspector.getByLabel("Name", { exact: true });
  await expect(name).toHaveValue("delivery route warehouse");
  await name.fill("delivery route depot");
  await name.press("Tab");
  await expect(page.getByRole("region", { name: "Inspector: delivery route depot" })).toBeVisible(slow);
  await expect(page.getByTestId("save-status")).toHaveText("Saved", slow);
  await page.getByLabel("Filter elements by name").fill("delivery route");
  await expect(page.getByTestId("explorer-row-delivery route depot")).toBeVisible();
  await expect(page.getByTestId("explorer-row-delivery route warehouse")).toHaveCount(0);
  await page.getByLabel("Filter elements by name").fill("");
  await expect(page.getByRole("tab", { name: /Problems/ })).not.toContainText(/[1-9]/, slow);

  // Generate: plan every pack, read a diff.
  await workspace(page, "Generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-result")).toContainText("succeeded", slow);
  const summary = page.getByTestId("plan-summary");
  await expect(summary).toContainText("sql-ddl:");
  await expect(summary).toContainText("csharp-dapper:");
  const rows = page.getByTestId("changes").getByRole("row");
  expect(await rows.count()).toBeGreaterThan(0);
  await page.locator("#filter-pack").selectOption("sql-ddl");
  const first = rows.first();
  const path = (await first.getByRole("cell").nth(1).innerText()).trim();
  await first.click();
  const diff = page.getByTestId("diff-viewer");
  await expect(diff).toContainText(path, slow);
  await expect(diff).toContainText("@@");
  await page.locator("#filter-pack").selectOption("");

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
