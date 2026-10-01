// Expanded rows keep the children their document gives them when the tree is rebuilt (explorer-redesign.md 4.4): Layout
// on a chart without a diagram creates one, which rebuilds the explorer's forest; an expanded process keeps its States
// and Events folders with their counts, and an expanded entity in the Domain model keeps its Attributes.
// Mock-only: InvoiceLifecycle has no diagram in the mock (mocks/model/processDiagramSeed.ts).
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const chevron = (row: Locator) => row.locator("span[aria-hidden]").first();

async function expanded(row: Locator) {
  if ((await row.getAttribute("aria-expanded")) !== "true") await chevron(row).click();
  await expect(row).toHaveAttribute("aria-expanded", "true");
}

test("Layout keeps an expanded process's States and Events, and an expanded entity's Attributes", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const tree = side.getByTestId("explorer-tree");

  // Domain model: Billing › Entities › Invoice, expanded with its Attributes.
  await expanded(side.getByTestId("explorer-domain-Billing"));
  await expanded(tree.getByTestId("explorer-folder-Entities").first());
  const invoice = tree.getByTestId("explorer-row-Invoice");
  await expanded(invoice);
  await expect(tree.getByTestId("explorer-folder-Attributes")).toHaveCount(1);

  // Processes: Billing › InvoiceLifecycle, expanded with its States and Events, then opened on its chart.
  await workspace(page, "Processes");
  await expanded(side.getByTestId("explorer-folder-Billing"));
  const lifecycle = side.getByTestId("explorer-row-InvoiceLifecycle");
  await expanded(lifecycle);
  const states = side.getByTestId("explorer-folder-States");
  const events = side.getByTestId("explorer-folder-Events");
  await expect(states).toContainText("4");
  await expect(events).toBeVisible();
  const eventCount = (await events.textContent())!.match(/\d+/)![0];
  await lifecycle.dblclick();
  const editor = page.getByTestId("process-editor");
  await expect(page.getByTestId("statechart-canvas")).toBeVisible();
  await expanded(lifecycle);
  await expect(states).toContainText("4");

  // Layout creates the chart's diagram: the forest is rebuilt, and the expanded row keeps its document's folders.
  await editor.getByTestId("chart-layout").click();
  await expect(page.getByTestId("statechart-canvas")).toHaveAttribute("data-diagram", /.+/);
  await expect(lifecycle).toHaveAttribute("aria-expanded", "true");
  await expect(states).toBeVisible();
  await expect(states).toContainText("4");
  await expect(events).toContainText(eventCount);
  await expect(side.getByTestId("explorer-folder-Scenarios")).toBeVisible();

  // Back in the Domain model, Invoice is still expanded with its Attributes.
  await workspace(page, "Domain model");
  await expect(invoice).toHaveAttribute("aria-expanded", "true");
  await expect(tree.getByTestId("explorer-folder-Attributes")).toHaveCount(1);
});
