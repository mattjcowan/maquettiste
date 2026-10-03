// Generate workspace (mock project): the plan summary lines, every file grouped by unit with what Apply does to it, the outcome
// chips and the other filters, the per-file diff in the bottom panel (none for a file Apply leaves alone), apply by plan id, job
// progress in Output, run history, and clearing it; the plan result line, and the Options popover (re-render every file, the
// hand-edit choice) sent with the plan request.
import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";

test("plan, read a diff, apply, output and history", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await expect(page.getByText("No plan yet")).toBeVisible();
  await expect(page.getByTestId("apply")).toBeDisabled();

  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-result")).toContainText(/Plan ready: \d+ files? to write \(\d+ added/);
  await expect(page.getByTestId("plan-result")).not.toContainText("hand edits");
  const summary = page.getByTestId("plan-summary");
  await expect(summary).toContainText("sql-ddl:");
  await expect(summary).toContainText("csharp-dapper:");
  const rows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  const all = await rows.count();
  expect(all).toBeGreaterThan(0);

  // The pack filter narrows the table to one pack.
  await page.locator("#filter-pack").selectOption("sql-ddl");
  await expect(rows.first()).toContainText("sql-ddl");
  expect(await rows.count()).toBeLessThan(all);
  await page.locator("#filter-pack").selectOption("");

  // Selecting a file shows its unified diff.
  const first = rows.first();
  const path = (await first.getByRole("gridcell").nth(1).innerText()).trim();
  await first.click();
  const diff = page.getByTestId("diff-viewer");
  await expect(diff).toContainText(path);
  await expect(diff).toContainText("@@");

  await page.getByTestId("apply").click();
  await expect(page.getByTestId("apply-result")).toContainText("succeeded");

  await page.getByRole("tab", { name: "Output" }).click();
  const output = page.getByTestId("output-list");
  await expect(output).toContainText("Apply succeeded");
  await expect(output).toContainText("Apply: render");

  const history = page.getByTestId("history").getByRole("listitem");
  await expect(history).toHaveCount(2);
  await expect(history.first()).toContainText("apply");

  // A second plan after the apply has nothing left to write: its units are skipped, so their files are not re-rendered.
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("nothing to write");
  await expect(page.getByTestId("plan-summary")).toContainText(/files? not re-rendered \(inputs unchanged\)/);
  await expect(page.getByTestId("plan-summary")).not.toContainText("to add");
  await expect(page.getByTestId("plan-summary")).not.toContainText("to modify");
  await expect(page.getByTestId("apply")).toBeDisabled();
  await expect(page.getByTestId("plan-nothing-to-write")).toContainText("Apply has nothing to do");
  await expect(page.getByTestId("plan-result")).toHaveText(/^Plan ready: nothing to write; \d+ not re-rendered \(inputs unchanged\), \d+ yours$/);
});

/** The number a filter chip shows. */
async function chipCount(page: Page, chip: string): Promise<number> {
  return Number(await page.getByTestId(`plan-filter-${chip}`).locator("span").innerText());
}

test("a plan lists every file with what Apply does to it; the chips filter it; files Apply leaves alone have no diff", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  const rows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  const result = page.getByTestId("plan-result");

  // First plan: everything is to write; All is the default and counts every file.
  await page.getByTestId("plan").click();
  await expect(result).toContainText(/^Plan ready: \d+ files to write \(\d+ added\)$/);
  await expect(page.getByTestId("plan-filter-all")).toHaveAttribute("aria-pressed", "true");
  const files = await chipCount(page, "all");
  expect(files).toBeGreaterThan(0);
  expect(await chipCount(page, "write")).toBe(files);
  await expect(page.getByTestId("changes-count")).toHaveText(new RegExp(`^${files} files in \\d+ units$`));
  await page.getByTestId("apply").click();
  await expect(page.getByTestId("apply-result")).toContainText("succeeded");

  // Plan again: every file is still listed, now as not re-rendered (or yours), and nothing is to write.
  await page.getByTestId("plan").click();
  await expect(result).toContainText("Plan ready: nothing to write;");
  await expect(page.getByTestId("plan-filter-all")).toContainText(String(files));
  expect(await chipCount(page, "all")).toBe(files);
  expect(await chipCount(page, "write")).toBe(0);
  await expect(page.getByTestId("plan-filter-write")).toBeDisabled();
  const notRendered = await chipCount(page, "not-rendered");
  const yours = await chipCount(page, "yours");
  expect(notRendered).toBeGreaterThan(0);
  expect(yours).toBeGreaterThan(0);
  expect(notRendered + yours).toBe(files);
  const quiet = rows.first().locator("[data-kind]");
  await expect(quiet).toHaveText(/^(not re-rendered|yours)$/);
  await expect(page.getByTestId("changes").locator('[data-kind="not-rendered"]').first()).toHaveAttribute(
    "title",
    "Its inputs did not change since the last run; Re-render every file renders it again",
  );

  // The chips filter the list: Not re-rendered, then Yours. A file that is yours has no diff, only what it is.
  await page.getByTestId("plan-filter-not-rendered").click();
  await expect(page.getByTestId("plan-filter-not-rendered")).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByTestId("changes-count")).toHaveText(new RegExp(`^${notRendered} files in `));
  await expect(rows.first().locator("[data-kind]")).toHaveText("not re-rendered");
  await rows.first().click();
  await expect(page.getByTestId("diff-quiet")).toContainText("Its inputs did not change since the last run");
  await page.getByTestId("plan-filter-yours").click();
  await expect(page.getByTestId("changes-count")).toHaveText(new RegExp(`^${yours} files in `));
  for (const badge of await page.getByTestId("changes").locator("[data-kind]").all()) await expect(badge).toHaveText("yours");
  await rows.first().click();
  await expect(page.getByTestId("diff-quiet")).toContainText("Written once; yours to edit. Generation never overwrites it.");
  await expect(page.getByTestId("diff-viewer")).toHaveCount(0);
  await page.getByTestId("plan-filter-all").click();
  await expect(page.getByTestId("changes-count")).toHaveText(new RegExp(`^${files} files in `));

  // Re-render every file: the files come out identical (or yours), none is "not re-rendered", and Apply still writes nothing.
  await page.getByTestId("generate-options").click();
  await expect(page.getByTestId("plan-option-force-description")).toHaveText(
    "Renders every file again. Apply still writes only files that differ, and never files that are yours.",
  );
  await page.getByTestId("plan-option-force").click();
  await page.keyboard.press("Escape");
  await page.getByTestId("plan").click();
  await expect(result).toHaveText(/^Plan ready: nothing to write; \d+ identical, \d+ yours$/);
  expect(await chipCount(page, "all")).toBe(files);
  expect(await chipCount(page, "not-rendered")).toBe(0);
  await expect(page.getByTestId("plan-filter-not-rendered")).toBeDisabled();
  await page.getByTestId("plan-filter-identical").click();
  await expect(rows.first().locator("[data-kind]")).toHaveText("identical");
  await rows.first().click();
  await expect(page.getByTestId("diff-quiet")).toContainText("identical to the file on disk");
  await expect(page.getByTestId("apply")).toBeDisabled();
});

test("plan options: re-render every file and the hand-edit choice go with the plan request", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  const options = page.getByTestId("generate-options");
  await expect(options).toHaveAttribute("title", "Plan options");
  await expect(page.getByTestId("generate-options-dot")).toHaveCount(0);

  await options.click();
  const panel = page.getByTestId("generate-options-panel");
  await expect(panel.getByTestId("plan-option-hand-edits").locator("option").first()).toHaveText(/^Project default \(.+\)$/);
  await panel.getByTestId("plan-option-force").click();
  await panel.getByTestId("plan-option-hand-edits").selectOption("overwrite");
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("generate-options-dot")).toBeVisible();

  const sent = page.waitForRequest((r) => r.method() === "POST" && new URL(r.url()).pathname === "/api/generate/plan");
  await page.getByTestId("plan").click();
  expect((await sent).postDataJSON()).toMatchObject({ force: true, handEdits: "overwrite" });
  await expect(page.getByTestId("plan-result")).toContainText("Plan ready");
  // Every unit renders again: the changes say why.
  const rows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  await expect(rows.first()).toContainText("Forced: the run renders every unit");

  // Back to the defaults: neither is sent.
  await options.click();
  await panel.getByTestId("plan-option-force").click();
  await panel.getByTestId("plan-option-hand-edits").selectOption("");
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("generate-options-dot")).toHaveCount(0);
  const plain = page.waitForRequest((r) => r.method() === "POST" && new URL(r.url()).pathname === "/api/generate/plan");
  await page.getByTestId("plan").click();
  const body = (await plain).postDataJSON() as Record<string, unknown>;
  expect(body.force).toBeUndefined();
  expect(body.handEdits).toBeUndefined();
});

test("clear the run history after a plan", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("sql-ddl:");
  await expect(page.getByTestId("apply")).toBeEnabled();
  const clear = page.getByTestId("clear-run-history");
  await expect(clear).toBeEnabled();
  await expect(clear).toHaveAttribute("title", "Clear the run history and its stored plans");

  await clear.click();
  await expect(page.getByTestId("clear-run-history-dialog")).toContainText("a plan not yet applied must be made again");
  await page.getByTestId("clear-run-history-confirm").click();

  await expect(page.getByTestId("history")).toHaveText("No runs yet.");
  await expect(page.getByTestId("apply")).toBeDisabled();
  await expect(page.getByText("No plan yet")).toBeVisible();
  await expect(clear).toBeDisabled();
  await expect(page.getByText(/Run history cleared: \d+ runs? and \d+ stored plans? removed\./)).toBeVisible();
});
