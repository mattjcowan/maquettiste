// Processes linked to operations, editor part 1 (processes-operations-design.md section 12): an event is added to an operation's
// Fires list through the two-step picker, the process's Events tab then names the operation under Fired by, and a click on it opens
// the operation again. Mock-only: the operation is created on the mock server, the lifecycle comes from mocks/model/processSeed.ts.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
const OPERATION = "01JR5NE0000000000000000200";
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function createOperation(page: Page) {
  const outcome = await page.evaluate(
    async (element) => {
      const res = await fetch("/api/model/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ operations: [{ op: "create", element }] }),
      });
      return (await res.json()) as { outcome: string };
    },
    {
      kind: "operation",
      id: OPERATION,
      name: "issueInvoice",
      package: BILLING,
      subject: INVOICE,
      effect: "change",
      outcomes: [{ id: "01JR5NE0000000000000000201", name: "issued" }],
    },
  );
  expect(outcome.outcome).toBe("saved");
}

async function expand(page: Page, row: Locator) {
  await row.click();
  await page.keyboard.press("ArrowRight");
  await expect(row).toHaveAttribute("aria-expanded", "true");
}

test("an event is added to an operation's Fires list and shows under Fired by on the process, which opens the operation", async ({ page }) => {
  await openEditor(page);
  await createOperation(page);

  // Open the operation from the Domain model explorer.
  const side = explorer(page);
  await expand(page, side.getByTestId("explorer-domain-Billing"));
  const folder = side.getByTestId("explorer-folder-Operations");
  await expand(page, folder);
  await side.getByTestId("explorer-row-issueInvoice").dblclick();
  const operation = page.getByRole("region", { name: "Editor: issueInvoice" });
  const fires = operation.getByRole("region", { name: "Fires" });
  await expect(fires).toContainText("This operation fires no events.");

  await fires.getByRole("button", { name: /Add event/ }).click();
  const dialog = page.getByTestId("add-event-dialog");
  await expect(dialog.getByRole("region", { name: "On Invoice" }).getByRole("button", { name: "InvoiceLifecycle" })).toBeVisible();
  await dialog.getByLabel("Filter processes").fill("lifecycle");
  await dialog.getByRole("button", { name: "InvoiceLifecycle" }).click();
  await dialog
    .getByRole("button", { name: /^issue/ })
    .first()
    .click();
  await expect(page.getByTestId("fired-event")).toContainText("InvoiceLifecycle › issue");

  // The process's Events tab names the operation.
  await workspace(page, "Processes");
  const processes = explorer(page);
  await expand(page, processes.getByTestId("explorer-folder-Billing"));
  for (const use of ["lifecycle", "orchestration"]) {
    const group = processes.locator(`[data-key$="/use:${use}"]`);
    if ((await group.count()) && (await group.getAttribute("aria-expanded")) !== "true") await expand(page, group);
  }
  await processes.getByTestId("explorer-row-InvoiceLifecycle").dblclick();
  const process = page.getByTestId("process-editor");
  await process.getByRole("tab", { name: "Events", exact: true }).click();
  const link = process.getByTestId("events-grid-row").filter({ hasText: "issue" }).first().getByRole("button", { name: "issueInvoice" });
  await expect(link).toBeVisible();

  await link.click();
  await expect(page.getByRole("region", { name: "Editor: issueInvoice" })).toBeVisible();
  await page.getByRole("button", { name: "Stop firing issue" }).click();
  await expect(page.getByTestId("fired-event")).toHaveCount(0);
});
