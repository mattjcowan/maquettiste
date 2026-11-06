// Leftovers of the lifecycle screens, in the mock: the replay loads the scenario's start and the cast names its attributes,
// the States grid keeps Name in view while it scrolls, and a warning alone shows the line under the editor's title.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

async function expand(page: Page, row: Locator) {
  await row.click();
  await page.keyboard.press("ArrowRight");
  await expect(row).toHaveAttribute("aria-expanded", "true");
}

async function openProcess(page: Page, group: "orchestration" | "lifecycle", row: string, url = "/") {
  await openEditor(page, url);
  await workspace(page, "Processes");
  const side = page.getByRole("complementary", { name: "Explorer" });
  await expand(page, side.getByTestId("explorer-folder-Billing"));
  await expand(page, side.locator(`[data-key$="/use:${group}"]`));
  await side.getByTestId(`explorer-row-${row}`).dblclick();
  const editor = page.getByTestId("process-editor");
  await expect(editor.getByRole("tab", { name: "Chart", exact: true })).toHaveAttribute("aria-selected", "true");
  return editor;
}

test("a replay loads the scenario's start, and the cast names its attributes", async ({ page }, info) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openEditor(page);
  // A scenario with a cast: a copy of BudgetRejected whose one object is an Invoice with a value keyed by an attribute id.
  await page.evaluate(async () => {
    const get = async (url: string) => (await fetch(url)).json();
    const index = (await get("/api/model/index")) as { elements?: { id: string; kind: string; name: string }[] } | { id: string; kind: string; name: string }[];
    const rows = Array.isArray(index) ? index : (index.elements ?? []);
    const byName = (kind: string, name: string) => rows.find((r) => r.kind === kind && r.name === name)!;
    const source = (await get(`/api/model/elements/${byName("scenario", "BudgetRejected").id}`)) as { json: Record<string, unknown> };
    const invoice = (await get(`/api/model/elements/${byName("entity", "Invoice").id}`)) as { json: { attributes: { id: string }[] } };
    const steps = (source.json.steps as Record<string, unknown>[]).map((s, i) => ({ ...s, id: `01JQSTP00000000000000009${10 + i}` }));
    const body = {
      ...source.json,
      id: "01JQSCN0000000000000000999",
      name: "CastDemo",
      subject: "01JQ0BJ0000000000000000001",
      steps,
      objects: [{ id: "01JQ0BJ0000000000000000001", name: "inv1", entity: byName("entity", "Invoice").id, values: { [invoice.json.attributes[0].id]: 42 } }],
    };
    const response = await fetch("/api/model/elements", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    if (!response.ok) throw new Error(await response.text());
  });
  const side = page.getByRole("complementary", { name: "Explorer" });
  await workspace(page, "Processes");
  await expand(page, side.getByTestId("explorer-folder-Billing"));
  await expand(page, side.locator('[data-key$="/use:orchestration"]'));
  await side.getByTestId("explorer-row-Purchase approval").dblclick();
  const editor = page.getByTestId("process-editor");
  const panel = editor.getByTestId("simulation-panel");
  const toggle = panel.getByRole("button", { name: /simulation panel/ }).first();
  if ((await toggle.getAttribute("aria-expanded")) !== "true") await toggle.click();
  await panel.getByTestId("simulation-replay").click();
  const dialog = page.getByTestId("replay-dialog");
  await dialog.getByLabel("Scenario").selectOption({ label: "CastDemo" });
  await dialog.getByTestId("replay-run").click();
  await expect(dialog).toHaveCount(0);
  // Start shows the scenario's value, not the default.
  await expect(panel.locator('[data-testid="start-row"][data-attribute="amount"] input')).toHaveValue("25000");
  // The cast shows the attribute's name (never its 26-character id) and the full values are in the row's tooltip.
  const cast = panel.getByTestId("start-object").first();
  await expect(cast).toBeVisible();
  await expect(cast).not.toContainText(/01[0-9A-Z]{20,}/);
  await expect(cast).toContainText(/\s42/);
  // While the replay is shown, Enabled does not offer inputs of the live session.
  await expect(panel.getByTestId("enabled-row")).toHaveCount(0);
  await page.screenshot({ path: info.outputPath("replay-cast.png") });
  await info.attach("replay-cast", { path: info.outputPath("replay-cast.png"), contentType: "image/png" });
});

test("the States grid keeps Name at the left while it scrolls", async ({ page }, info) => {
  await page.setViewportSize({ width: 1100, height: 700 });
  const editor = await openProcess(page, "orchestration", "Purchase approval");
  await editor.getByRole("tab", { name: "States", exact: true }).click();
  const grid = editor.getByRole("grid").first();
  await expect(grid).toBeVisible();
  const scroller = grid.locator("xpath=..");
  const scrolls = await scroller.evaluate((el) => el.scrollWidth > el.clientWidth);
  await scroller.evaluate((el) => (el.scrollLeft = el.scrollWidth));
  const name = grid.locator("th").first();
  const box = await name.boundingBox();
  const frame = await scroller.boundingBox();
  expect(Math.abs(box!.x - frame!.x)).toBeLessThan(4);
  info.annotations.push({ type: "grid-scrolls-sideways", description: String(scrolls) });
  await page.screenshot({ path: info.outputPath("states-grid.png") });
  await info.attach("states-grid", { path: info.outputPath("states-grid.png"), contentType: "image/png" });
});

test("a warning alone shows the line under the title", async ({ page }, info) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  const editor = await openProcess(page, "lifecycle", "InvoiceLifecycle", "/?mock=lifecycle");
  const line = editor.getByTestId("editor-findings");
  await expect(line).toContainText(/\d+ warnings?/);
  await page.screenshot({ path: info.outputPath("warning-line.png") });
  await info.attach("warning-line", { path: info.outputPath("warning-line.png"), contentType: "image/png" });
});
