// The explorer and the canvas in step (explorer-redesign.md 3.5, step 10): membership dots, drag and drop from the tree
// onto the diagram, Show on canvas. Used, breadcrumbs, go to definition and history (3.3, step 11).
import { expect, openEditor, test } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });

test("a relationship dragged from the tree onto the diagram becomes a member, and Show on canvas centres a card", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const canvas = page.getByTestId("canvas");
  await expect(page.locator(".react-flow__edge")).toHaveCount(3);

  await side.getByLabel("Search the model").fill("refers");
  const refersTo = side.getByTestId("explorer-row-refers to");
  await expect(refersTo).toBeVisible();
  // Not on the diagram yet: no membership dot.
  await expect(refersTo.locator('[data-part="on-canvas"]')).toHaveCount(0);
  await refersTo.dragTo(canvas, { targetPosition: { x: 80, y: 80 } });
  await expect(page.locator(".react-flow__edge")).toHaveCount(4);
  await expect(refersTo.locator('[data-part="on-canvas"]')).toBeVisible();
  // Dropping it again adds nothing.
  await refersTo.dragTo(canvas, { targetPosition: { x: 120, y: 120 } });
  await expect(page.getByText("Already on the diagram.")).toBeVisible();
  await expect(page.locator(".react-flow__edge")).toHaveCount(4);

  // Show on canvas selects the card and centres it.
  await side.getByLabel("Search the model").fill("Payment");
  const payment = side.getByTestId("explorer-row-Payment");
  await expect(payment.locator('[data-part="on-canvas"]')).toBeVisible();
  await payment.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Show on canvas" }).click();
  const id = await page.locator('.react-flow__node[aria-label="Entity Payment"]').getAttribute("data-id");
  await expect(canvas).toHaveAttribute("data-centered", id!);
});

test("Used lists the references, goes to one, and history and go to definition move the selection", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").click();
  await side.getByLabel("Search the model").fill("");
  const crumbs = page.getByTestId("breadcrumbs");
  await expect(crumbs.getByRole("button", { name: "Invoice", exact: true })).toHaveAttribute("aria-current", "page");
  await expect(crumbs).toContainText("Domain model");
  await expect(crumbs).toContainText("Billing");
  await expect(crumbs).toContainText("Entities");

  // Shift+F12: the References tab, grouped by kind, with the diagram membership.
  await side.getByRole("tree", { name: "Domain model" }).focus();
  await page.keyboard.press("Shift+F12");
  const panel = page.getByTestId("references-panel");
  await expect(panel).toHaveAttribute("data-complete", "true");
  await expect(page.getByTestId("tab-references")).toHaveAttribute("data-state", "active");
  await expect(panel.getByTestId("references-title")).toContainText("Entity Invoice");
  await expect(panel.getByTestId("reference-row").filter({ hasText: "on diagram Billing overview" })).toBeVisible();
  await expect(panel.getByTestId("references-kind").filter({ hasText: "Relationships" })).toBeVisible();

  // A row goes to the referencing element.
  await panel
    .getByTestId("reference-row")
    .filter({ hasText: /^contains/ })
    .click();
  await expect(crumbs.getByRole("button", { name: "contains", exact: true })).toHaveAttribute("aria-current", "page");
  await expect(page.getByRole("region", { name: /^Inspector: / })).toContainText("contains");

  // Go to definition (F12) on the relationship's end entity.
  const end = page.locator("#end-1-entity");
  const target = await end.getAttribute("data-definition");
  expect(target).toBeTruthy();
  await end.focus();
  await page.keyboard.press("F12");
  await expect(crumbs.getByRole("button", { name: "InvoiceLine", exact: true })).toHaveAttribute("aria-current", "page");

  // History: back to the relationship, back to Invoice, forward again.
  await page.getByTestId("history-back").click();
  await expect(crumbs.getByRole("button", { name: "contains", exact: true })).toHaveAttribute("aria-current", "page");
  await side.getByRole("tree", { name: "Domain model" }).focus();
  await page.keyboard.press("Alt+ArrowLeft");
  await expect(crumbs.getByRole("button", { name: "Invoice", exact: true })).toHaveAttribute("aria-current", "page");
  await page.keyboard.press("Alt+ArrowRight");
  await expect(crumbs.getByRole("button", { name: "contains", exact: true })).toHaveAttribute("aria-current", "page");
  await expect(page.getByTestId("history-forward")).toBeEnabled();
});
