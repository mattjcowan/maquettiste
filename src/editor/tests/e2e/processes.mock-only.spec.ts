// The Processes explorer (phase-3-design.md 6.1): the rail item, the tree walked by keyboard, the process editor opened
// on a state's tab, the New process, New actor and New scenario dialogs, Verify scenarios, Export and Import XState,
// and a process deleted with its scenarios. Mock-only: the mock model carries the processes (mocks/model/processSeed.ts).
import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const menuItem = (page: Page, name: string) => page.getByTestId("row-menu").getByRole("menuitem", { name });

async function openProcesses(page: Page) {
  await openEditor(page);
  await workspace(page, "Processes");
  const side = explorer(page);
  await expect(side.getByTestId("explorer-totals")).toHaveText("2 processes · 3 actors · 3 scenarios");
  return side;
}

async function expand(page: Page, row: import("@playwright/test").Locator) {
  await row.click();
  await page.keyboard.press("ArrowRight");
  await expect(row).toHaveAttribute("aria-expanded", "true");
}

test("the Processes explorer walks domains, processes, states and actors by keyboard", async ({ page }) => {
  const side = await openProcesses(page);
  const rail = page.getByRole("navigation", { name: "Explorers" }).locator("[data-testid^='rail-']");
  expect((await rail.evaluateAll((els) => els.map((e) => e.getAttribute("aria-label")))).slice(0, 6)).toEqual([
    "Domain model",
    "Processes",
    "Reference data",
    "Databases",
    "Diagrams",
    "Generate",
  ]);
  const tree = side.getByRole("tree", { name: "Processes" });
  await expect(side.getByTestId("explorer-folder-Catalog")).toHaveCount(0);
  const billing = side.getByTestId("explorer-folder-Billing");
  await expect(billing).toContainText("2 processes");
  await expand(page, billing);
  const purchase = side.getByTestId("explorer-row-Purchase approval");
  await expect(purchase).toContainText("orchestration · 13 states");
  await expand(page, purchase);
  await expect(side.getByTestId("explorer-folder-States")).toContainText("13");
  await expect(side.getByTestId("explorer-folder-Events")).toContainText("4");
  await expect(side.getByTestId("explorer-folder-Scenarios")).toBeVisible();

  // A state row opens the process editor on States with the state selected (Enter pins it).
  await expand(page, side.getByTestId("explorer-folder-States").first());
  await tree.focus();
  await page.keyboard.type("Drafting");
  await page.keyboard.press("Enter");
  const editor = page.getByTestId("process-editor");
  await expect(editor).toHaveAttribute("data-id", "01JQPRC0000000000000000002");
  await expect(editor.getByRole("tab", { name: "States" })).toHaveAttribute("aria-selected", "true");
  await expect(editor.locator('[data-testid="states-grid-row"][data-id="01JQSTA0000000000000000101"] [aria-selected="true"]')).toHaveCount(1);

  // A scenario row opens the Scenarios tab.
  await expand(page, side.getByTestId("explorer-folder-Scenarios").first());
  await side.getByTestId("explorer-row-Budget rejected").dblclick();
  await expect(editor.getByRole("tab", { name: "Scenarios" })).toHaveAttribute("aria-selected", "true");
  await expect(editor.locator('[data-testid="scenarios-grid-row"][data-id="01JQSCN0000000000000000010"] [aria-selected="true"]')).toHaveCount(1);

  // A lifecycle names its bound attribute, collapsed and expanded.
  const lifecycle = side.getByTestId("explorer-row-InvoiceLifecycle");
  await expect(lifecycle).toContainText("lifecycle · Invoice.status · 4 states");
  await expand(page, lifecycle);
  await expect(lifecycle).toContainText("lifecycle · Invoice.status · 4 states");

  // Actors: type and stereotypes.
  await expand(page, side.getByTestId("explorer-folder-Actors"));
  await expect(side.getByTestId("explorer-row-BudgetHolder")).toContainText("person · persona");
  await expect(side.getByTestId("explorer-row-ProcurementSystem")).toContainText("external system");
  expect((await new AxeBuilder({ page }).include('[data-testid="explorer-processes"]').analyze()).violations).toEqual([]);

  // The Domain model lists the same processes in the domain's Processes folder, and the actors under People and access.
  await workspace(page, "Domain model");
  const model = explorer(page);
  await expand(page, model.getByTestId("explorer-domain-Billing"));
  await expand(page, model.getByTestId("explorer-folder-Processes"));
  await expect(model.getByTestId("explorer-row-Purchase approval")).toBeVisible();
  await expect(model.getByTestId("explorer-folder-People and access")).toBeVisible();
});

test("New process, New actor and New scenario each save as one change", async ({ page }) => {
  const side = await openProcesses(page);
  await side.getByTestId("explorer-new").click();
  await page.getByRole("menuitem", { name: "New process…" }).click();
  const dialog = page.getByTestId("new-process-dialog");
  await expect(dialog).toBeVisible();
  expect((await new AxeBuilder({ page }).include('[data-testid="new-process-dialog"]').analyze()).violations).toEqual([]);
  await dialog.getByLabel("Name").fill("CustomerLifecycle");
  await dialog.getByLabel("Domain").selectOption({ label: "Billing" });
  await dialog.getByLabel("Subject entity").selectOption({ label: "Customer" });
  await expect(dialog.getByLabel("Bound attribute")).toHaveValue("@new");
  await expect(dialog).toContainText("Starts with one state, Initial.");
  await dialog.getByTestId("new-process-create").click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByTestId("element-editor")).toHaveAttribute("data-kind", "process");
  const billing = side.getByTestId("explorer-folder-Billing");
  await expand(page, billing);
  await expect(side.getByTestId("explorer-row-CustomerLifecycle")).toContainText("lifecycle · Customer.status · 1 state");

  // An existing enum: one state per member.
  await billing.click({ button: "right" });
  await menuItem(page, "New process…").click();
  await dialog.getByLabel("Name").fill("InvoiceFlow");
  await dialog.getByLabel("Subject entity").selectOption({ label: "Invoice" });
  await expect(dialog.getByLabel("Bound attribute")).toHaveValue("01J92P0V0VB1Z49SWERMAGR4TV");
  await expect(dialog).toContainText("Starts with 4 states, one per member.");
  await dialog.getByTestId("new-process-use-orchestration").check();
  await dialog.getByLabel("Subject entity (optional)").selectOption({ label: "None" });
  await dialog.getByTestId("new-process-create").click();
  await expect(side.getByTestId("explorer-row-InvoiceFlow")).toContainText("orchestration · 1 state");

  // New actor: a persona stereotype shows the goals field.
  await side.getByTestId("explorer-new").click();
  await page.getByRole("menuitem", { name: "New actor…" }).click();
  const actor = page.getByTestId("new-actor-dialog");
  await actor.getByLabel("Name").fill("Clerk");
  await expect(actor.getByLabel("Goals")).toHaveCount(0);
  await actor.getByTestId("new-actor-stereotype-persona").check();
  await actor.getByLabel("Goals").fill("Close the month");
  await actor.getByTestId("new-actor-create").click();
  await expect(actor).toHaveCount(0);
  await expand(page, side.getByTestId("explorer-folder-Actors"));
  await expect(side.getByTestId("explorer-row-Clerk")).toContainText("person · persona");

  // New scenario from a process's Scenarios folder (Record from simulation: simulation.mock-only.spec.ts).
  await expand(page, side.getByTestId("explorer-row-Purchase approval"));
  await side.getByTestId("explorer-folder-Scenarios").first().click({ button: "right" });
  await menuItem(page, "New scenario…").click();
  const scenario = page.getByTestId("new-scenario-dialog");
  await expect(scenario.getByTestId("new-scenario-record")).toBeEnabled();
  // The folder's process is preset, although it is not the first process alphabetically.
  await expect(scenario.locator("#new-scenario-process option:checked")).toHaveText(/Purchase/);
  await expect(scenario).toContainText("opens the chart with the simulation panel recording");
  await scenario.getByLabel("Name").fill("Smoke");
  await scenario.getByTestId("new-scenario-create").click();
  await expect(scenario).toHaveCount(0);
  await expect(side.getByTestId("explorer-row-Smoke")).toContainText("1 step · not run");

  // One change per dialog: undo removes the scenario.
  await page.keyboard.press("ControlOrMeta+z");
  await expect(side.getByTestId("explorer-row-Smoke")).toHaveCount(0);
});

test("Verify scenarios, Export XState, Import XState and Delete from the row menus", async ({ page }) => {
  const side = await openProcesses(page);
  const billing = side.getByTestId("explorer-folder-Billing");
  await expand(page, billing);
  const purchase = side.getByTestId("explorer-row-Purchase approval");

  await purchase.click({ button: "right" });
  await expect(menuItem(page, "Simulate")).not.toHaveAttribute("aria-disabled", "true");
  await menuItem(page, "Verify scenarios").click();
  await expect(page.getByTestId("output-list")).toContainText("PurchaseApproval: 1 of 2 scenarios passed");
  await expect(page.getByTestId("output-list")).toContainText("QuickApproval: failed at step 2");
  await expand(page, purchase);
  await expect(side.getByTestId("explorer-folder-Scenarios").first()).toContainText("✓ 1 passed");
  await expand(page, side.getByTestId("explorer-folder-Scenarios").first());
  await expect(side.getByTestId("explorer-row-Quick approval")).toContainText("failed at step 2");

  await purchase.click({ button: "right" });
  const download = page.waitForEvent("download");
  await menuItem(page, "Export XState").click();
  expect((await download).suggestedFilename()).toBe("PurchaseApproval.xstate.json");

  await billing.click({ button: "right" });
  await menuItem(page, "Import XState…").click();
  const dialog = page.getByTestId("import-xstate-dialog");
  await dialog.getByLabel("Machine config").fill('{ "id": "Door", "initial": "Closed", "states": { "Closed": { "on": { "open": "Opened" } }, "Opened": {} } }');
  await dialog.getByTestId("import-xstate-preview-button").click();
  await expect(page.getByTestId("import-xstate-preview")).toContainText("States: Closed, Opened");
  await expect(page.getByTestId("import-xstate-preview")).toContainText("Events: open");
  await dialog.getByTestId("import-xstate-apply").click();
  await expect(dialog).toHaveCount(0);
  await expect(side.getByTestId("explorer-row-Door")).toContainText("orchestration · 2 states");

  // Deleting a process deletes its scenarios, as the dialog says.
  await side.getByTestId("explorer-row-InvoiceLifecycle").click({ button: "right" });
  await menuItem(page, "Delete").click();
  await expect(page.getByRole("dialog")).toContainText("Its scenario is deleted with it.");
  await page.getByTestId("confirm-delete").click();
  await expect(side.getByTestId("explorer-row-InvoiceLifecycle")).toHaveCount(0);
  await expect(side.getByTestId("explorer-totals")).toHaveText("2 processes · 3 actors · 2 scenarios");
});
