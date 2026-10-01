// The plan explained on the Generate screen (generation-ui.md 4, mock project): the summary line per pack, the changes
// grouped by unit with counts, a file's pack, unit, template, element, output path and reason, the unit filter,
// "Why not?" on an unchanged unit and Explain for any unit.
import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "./fixtures";

test("the plan explains itself: groups, why this file, why not, explain", async ({ page }) => {
  await page.goto("/generate");
  await page.getByTestId("plan").click();
  const summary = page.getByTestId("plan-summary");
  await expect(summary).toContainText(/sql-ddl: \d+ units?, \d+ files? to add/);
  await expect(summary).toContainText(/csharp-dapper: \d+ units?, \d+ files? to add/);
  // The written files by cause ("New: no recorded state ...: N files") and by output root.
  await expect(page.getByTestId("plan-cause").first()).toContainText(/New: no recorded state from an earlier run: \d+ files?/);
  await expect(page.getByTestId("plan-root").first()).toContainText(/: \d+ files? \(\d+ to add/);

  // Grouped by unit, with the template and the counts in the header.
  const group = page.getByTestId("unit-group-sql-ddl/table");
  await expect(group).toContainText("table.scriban");
  await expect(group).toContainText(/\d+ files: \d+ to add/);
  const rows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  const all = await rows.count();

  // The first table file explains itself: pack, unit, template, element, output and why.
  const first = page.getByTestId("changes").locator('[data-testid^="change-db/"]').first();
  const path = (await first.getByRole("gridcell").nth(1).innerText()).trim();
  await first.click();
  const why = page.getByTestId("why-panel");
  await expect(why).toContainText("sql-ddl");
  await expect(why).toContainText(path);
  await expect(why).toContainText(".scriban");
  await expect(why).toContainText("New: no recorded state");
  await why.getByTestId("why-read-keys").click();
  await expect(why.getByTestId("why-not-answer")).toContainText("Renders (new)");

  // Collapsing a group hides its files; the unit filter keeps one unit.
  await group.click();
  await expect(group).toHaveAttribute("aria-expanded", "false");
  expect(await rows.count()).toBeLessThan(all);
  await group.click();
  await page.locator("#filter-unit").selectOption("sql-ddl/schema");
  await expect(page.getByTestId("unit-group-sql-ddl/table")).toHaveCount(0);
  await expect(page.getByTestId("unit-group-sql-ddl/schema")).toBeVisible();
  await page.locator("#filter-unit").selectOption("");

  const axe = await new AxeBuilder({ page }).include('[data-testid="plan-screen"]').analyze();
  expect(axe.violations.map((v) => v.id)).toEqual([]);

  // After the apply, a new plan skips every unit; "Why not?" asks the API.
  await page.getByTestId("apply").click();
  await expect(page.getByTestId("apply-result")).toContainText("succeeded");
  await page.getByTestId("plan").click();
  await expect(summary).toContainText("nothing to write");
  await expect(summary).toContainText(/all \d+ files already match the disk/);
  await expect(summary).toContainText(/\d+ units unchanged/);
  await expect(page.getByTestId("plan-nothing-to-write")).toContainText("Every file this plan renders is identical to the file on disk");
  // The table shows what changes (kept files here); Show: unchanged lists the files that already match the disk.
  const changeRows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  await expect(changeRows.filter({ hasText: /^unchanged/ })).toHaveCount(0);
  await page.locator("#filter-kind").selectOption("unchanged");
  await expect(changeRows.first()).toContainText("unchanged");
  await page.locator("#filter-kind").selectOption("changed");
  const unchanged = page.getByTestId("unchanged-units");
  await unchanged.getByRole("button", { name: /Unchanged units/ }).click();
  const entity = unchanged.locator('[data-testid^="unchanged-csharp-dapper/entity:"]').first();
  const key = (await entity.getAttribute("data-testid"))!.slice("unchanged-".length);
  await entity.getByRole("button", { name: "Why not?" }).click();
  await expect(entity.getByTestId("why-not-answer")).toContainText("Skipped: its");
  await expect(entity.getByTestId("why-not-answer")).toContainText("element (1)");

  // Explain answers for any unit and element, planned or not.
  const explain = page.getByTestId("explain");
  await explain.locator("#explain-pack").selectOption("csharp-dapper");
  await explain.locator("#explain-unit").fill("entity");
  await explain.locator("#explain-element").fill(key.split(":")[1]);
  await explain.getByTestId("explain-run").click();
  await expect(explain.getByTestId("explain-answer")).toContainText("unchanged");
  await expect(explain.getByTestId("explain-answer")).toContainText("Skipped");
  await explain.locator("#explain-unit").fill("no-such-unit");
  await explain.getByTestId("explain-run").click();
  await expect(explain.getByTestId("explain-answer")).toContainText("unknown-unit");
});
