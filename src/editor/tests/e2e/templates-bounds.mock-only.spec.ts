// What writing a template may render (generation-ui.md 5.2, "Bounds"; mock project). Opening a template renders ONE
// element and lists the unit's scope without rendering it; "Preview a list…" renders the few elements picked, when asked;
// a unit whose one render covers the whole model or a database renders only when asked, follows the typing while its
// render is fast, and says "out of date" once it is slow; the Database screen previews the whole database only when asked.
import type { Page, Request } from "@playwright/test";
import { expect, keepDdlOpen, test, workspace } from "./fixtures";

/** The preview and path-listing requests the page sends, with their bodies. */
function recordTemplateRequests(page: Page) {
  const sent: { path: string; body: Record<string, unknown> }[] = [];
  page.on("request", (r: Request) => {
    const path = new URL(r.url()).pathname;
    if (r.method() === "POST" && (path === "/api/templates/preview" || path === "/api/templates/paths"))
      sent.push({ path, body: (r.postDataJSON() ?? {}) as Record<string, unknown> });
  });
  return {
    previews: () => sent.filter((s) => s.path === "/api/templates/preview"),
    listings: () => sent.filter((s) => s.path === "/api/templates/paths"),
    /** Forgets what was sent so far (the Units tab the pack editor opens on renders its selected row's example path). */
    clear: () => void sent.splice(0),
  };
}

async function openTemplate(page: Page, pack: string, file: string, requests: ReturnType<typeof recordTemplateRequests>) {
  await page.goto("/generate");
  await workspace(page, "Generate");
  await page.getByRole("tree", { name: "Packs" }).getByTestId(`pack-row-p:${pack}`).getByText(pack, { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  await page.waitForTimeout(400);
  requests.clear();
  await page.keyboard.press("Alt+3");
  const tab = page.getByTestId("templates-tab");
  await tab.getByRole("navigation", { name: "Pack files" }).getByRole("button", { name: file, exact: true }).click();
  await expect(tab.getByTestId("template-path")).toHaveText(file);
  return tab;
}

test("opening a template renders one element, and a list of elements renders only when asked", async ({ page }) => {
  const requests = recordTemplateRequests(page);
  await openTemplate(page, "sql-ddl", "table.scriban", requests);
  await expect(page.getByTestId("preview-unit")).toHaveValue("table");
  await expect(page.getByTestId("preview-text").first()).not.toBeEmpty();
  // One element rendered; the scope listed without rendering any (no elementIds).
  await page.waitForTimeout(600);
  expect(requests.previews()).toHaveLength(1);
  for (const listing of requests.listings()) expect(listing.body.elementIds ?? []).toEqual([]);

  // Preview a list…: tick three tables (at most 20), then render them, one request each.
  await page.getByTestId("preview-list-open").click();
  const picker = page.getByTestId("preview-list-picker");
  await expect(picker).toBeVisible();
  const options = picker.getByTestId("preview-list-option");
  // The rendered element starts ticked; tick two more.
  await options.nth(1).check();
  await options.nth(2).check();
  await expect(page.getByTestId("preview-list-count")).toHaveText("3 of at most 20");
  await page.getByTestId("preview-list-run").click();
  await expect(page.getByTestId("preview-list-item")).toHaveCount(3);
  await expect(page.getByTestId("preview-list-item").first().getByTestId("preview-text")).not.toBeEmpty();
  expect(requests.previews()).toHaveLength(4);

  // Back to one table.
  await page.getByTestId("preview-list-close").click();
  await expect(page.getByTestId("preview-list-item")).toHaveCount(0);
  await expect(page.getByTestId("preview-text").first()).not.toBeEmpty();
});

test("a unit that renders the whole model previews only when asked", async ({ page }) => {
  const requests = recordTemplateRequests(page);
  await openTemplate(page, "csharp-dapper", "registrations.scriban", requests);
  await expect(page.getByTestId("preview-unit")).toHaveValue("registrations");
  const ask = page.getByTestId("preview-wide");
  await expect(ask).toHaveText("Preview (renders the whole model)");
  await expect(page.getByTestId("preview-wide-note")).toContainText("renders the whole model in one go");
  await page.waitForTimeout(600);
  expect(requests.previews()).toHaveLength(0);

  await ask.click();
  await expect(page.getByTestId("preview-wide-note")).toHaveCount(0);
  await expect.poll(() => requests.previews().length).toBe(1);
  expect(requests.previews()[0].body.elementId ?? null).toBeNull();
});

test("a unit rendered once per database previews only when asked, follows the typing while fast, and goes out of date once slow", async ({ page }) => {
  const requests = recordTemplateRequests(page);
  await openTemplate(page, "sql-ddl", "schema.scriban", requests);
  await expect(page.getByTestId("preview-unit")).toHaveValue("schema");
  await expect(page.getByTestId("preview-wide")).toHaveText("Preview (renders a whole database)");
  await page.waitForTimeout(600);
  expect(requests.previews()).toHaveLength(0);
  await page.getByTestId("preview-wide").click();
  await expect(page.getByTestId("preview-text").first()).toContainText("CREATE SCHEMA");
  expect(requests.previews()).toHaveLength(1);

  // Fast: the typing renders again by itself.
  await page.getByTestId("code-scriban").locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n-- typed while fast");
  await expect(page.getByTestId("preview-text").first()).toContainText("-- typed while fast");

  // Slow (the server reports 5 s): the next change still renders once (the last render was fast), then the text goes
  // out of date instead of rendering the whole database on every keystroke.
  await page.evaluate(() => ((window as unknown as { __mqMock: { previewElapsedMs: number | null } }).__mqMock.previewElapsedMs = 5000));
  await page.keyboard.type("\n-- one more");
  await expect(page.getByTestId("preview-text").first()).toContainText("-- one more");
  const before = requests.previews().length;
  await page.keyboard.type("\n-- typed while slow");
  await expect(page.getByTestId("preview-stale")).toContainText("Out of date");
  await page.waitForTimeout(600);
  expect(requests.previews()).toHaveLength(before);
  await page.getByTestId("preview-again").click();
  await expect(page.getByTestId("preview-text").first()).toContainText("-- typed while slow");
  expect(requests.previews()).toHaveLength(before + 1);
});

test("the Database screen previews the whole database only when asked", async ({ page }) => {
  await keepDdlOpen(page);
  const requests = recordTemplateRequests(page);
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/schema · whole database");
  await expect(page.getByTestId("ddl-preview-whole")).toBeVisible();
  await page.waitForTimeout(600);
  expect(requests.previews()).toHaveLength(0);
  await page.getByTestId("ddl-preview-whole").click();
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE SCHEMA");
  expect(requests.previews()).toHaveLength(1);
  // A picked table renders on its own, at once.
  await page.getByTestId("database-workspace").getByTestId("database-table-invoices").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/table · invoices");
  await expect(page.getByTestId("ddl-preview")).toContainText("invoices");
});
