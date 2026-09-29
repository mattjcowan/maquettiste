// The stage walk (phase2-design.md §4.10, mock project): open the editor, see Billing overview, select
// Invoice, rename an attribute in the inspector, watch the Problems panel follow, open Database and read
// the DDL preview, then plan and apply.
import { card, expect, openEditor, test, workspace } from "./fixtures";

test("open, select Invoice, rename, problems, DDL, plan and apply", async ({ page }) => {
  await openEditor(page);
  await expect(card(page, "Invoice")).toBeVisible();

  // Select Invoice on the canvas: the inspector and the URL follow.
  await card(page, "Invoice").click();
  const inspector = page.getByRole("region", { name: "Inspector: Invoice" });
  await expect(inspector.getByRole("heading", { name: "Invoice", level: 2 })).toBeVisible();
  await expect(page).toHaveURL(/[?&]sel=/);
  const grid = page.getByTestId("attribute-grid");
  const nameCells = grid.locator('td[data-column="name"]');
  await expect(nameCells.nth(1)).toHaveText("number");

  // A valid rename saves and the card changes with it.
  await nameCells.nth(1).dblclick();
  await grid.getByRole("textbox", { name: "Name of number" }).fill("invoiceNumber");
  await grid.getByRole("textbox", { name: "Name of number" }).press("Enter");
  await expect(card(page, "Invoice").getByText("invoiceNumber")).toBeVisible();
  await expect(page.getByTestId("save-status")).toHaveText("Saved");
  await expect(page.getByRole("tab", { name: /Problems/ })).toContainText("0");

  // An invalid rename (a duplicate name) is refused with 422 and shows up in Problems.
  await nameCells.nth(1).dblclick();
  await grid.getByRole("textbox").fill("issuedOn");
  await grid.getByRole("textbox").press("Enter");
  await expect(page.getByTestId("save-status")).toContainText("invalid");
  await expect(page.getByRole("tab", { name: /Problems/ })).toContainText("1");
  const problems = page.getByTestId("problems-list");
  await expect(problems).toContainText("MQ3007");
  await expect(problems).toContainText("Invoice");

  // Fixing it clears the problem.
  await nameCells.nth(1).dblclick();
  await grid.getByRole("textbox").fill("invoiceNo");
  await grid.getByRole("textbox").press("Enter");
  await expect(page.getByTestId("save-status")).toHaveText("Saved");
  await expect(page.getByRole("tab", { name: /Problems/ })).toContainText("0");

  // Database: tables and the DDL preview of the whole schema.
  await workspace(page, "Databases");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await expect(page.locator(".react-flow__node")).toHaveCount(6);
  const ddl = page.getByTestId("ddl-preview");
  await expect(ddl).toContainText("sql-ddl/schema");
  await expect(ddl).toContainText("CREATE SCHEMA IF NOT EXISTS billing;");
  // Picking the invoices table previews its own DDL, with the renamed column.
  await page.getByRole("group", { name: "Table invoices", exact: true }).click();
  await expect(ddl).toContainText("sql-ddl/table");
  await expect(ddl).toContainText("CREATE TABLE");
  await expect(ddl).toContainText("invoice_no");

  // Generate: plan with progress, then apply.
  await workspace(page, "Generate");
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("to add");
  await expect(page.getByTestId("plan-result")).toContainText("succeeded");
  await page.getByTestId("apply").click();
  await expect(page.getByTestId("job-progress")).toBeVisible();
  const result = page.getByTestId("apply-result");
  await expect(result).toContainText("succeeded");
  await expect(result).toContainText(/\d+ written/);
  await expect(page.getByTestId("history").getByRole("listitem")).toHaveCount(2);
});
