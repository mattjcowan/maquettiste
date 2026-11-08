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

/** Opens a folder or row unless it is open already (new folders appear closed; a created process may be revealed open). */
async function ensureOpen(page: Page, row: import("@playwright/test").Locator) {
  if ((await row.getAttribute("aria-expanded")) !== "true") await expand(page, row);
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
  // A domain's processes sit in a folder per use; only the uses it has show.
  await expect(side.locator('[data-key$="/use:lifecycle"]')).toBeVisible();
  await expect(side.locator('[data-key$="/use:library"]')).toHaveCount(0);
  await expand(page, side.locator('[data-key$="/use:orchestration"]'));
  const purchase = side.getByTestId("explorer-row-Purchase approval");
  await expect(purchase).toContainText("orchestration · 13 states");
  await expand(page, purchase);
  await expect(side.getByTestId("explorer-folder-States")).toContainText("13");
  await expect(side.getByTestId("explorer-folder-Events")).toContainText("4");
  await expect(side.getByTestId("explorer-folder-Guards and actions")).toBeVisible();
  await expect(side.getByTestId("explorer-folder-Gates")).toBeVisible();
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
  await expand(page, side.locator('[data-key$="/use:lifecycle"]'));
  const lifecycle = side.getByTestId("explorer-row-InvoiceLifecycle");
  await expect(lifecycle).toContainText("lifecycle · Invoice.status · 4 states");
  await expand(page, lifecycle);
  await expect(lifecycle).toContainText("lifecycle · Invoice.status · 4 states");

  // Actors by type (People, Roles, Systems): type and stereotypes.
  await expand(page, side.getByTestId("explorer-folder-Actors"));
  await expect(side.getByTestId("explorer-folder-Roles")).toBeVisible();
  await expand(page, side.getByTestId("explorer-folder-People"));
  await expand(page, side.getByTestId("explorer-folder-Systems"));
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
  await ensureOpen(page, side.locator('[data-key$="/use:lifecycle"]'));
  await expect(side.getByTestId("explorer-row-CustomerLifecycle")).toContainText("lifecycle · Customer.status · 1 state");

  // An existing enum: one state per member.
  await billing.click({ button: "right" });
  await menuItem(page, "New process…").click();
  await dialog.getByLabel("Name").fill("InvoiceFlow");
  await dialog.getByLabel("Subject entity").selectOption({ label: "Invoice" });
  await expect(dialog.getByLabel("Bound attribute")).toHaveValue("01J92P0V0VB1Z49SWERMAGR4TV");
  await expect(dialog).toContainText("Starts with 4 states, one per member.");
  await expect(dialog.getByTestId("new-process-use-meaning")).toContainText("A lifecycle describes the states one entity goes through");
  await dialog.getByTestId("new-process-use-orchestration").check();
  await expect(dialog.getByTestId("new-process-use-meaning")).toContainText("An orchestration coordinates work");
  await dialog.getByLabel("Subject entity (optional)").selectOption({ label: "None" });
  await dialog.getByTestId("new-process-create").click();
  await ensureOpen(page, side.locator('[data-key$="/use:orchestration"]'));
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
  await ensureOpen(page, side.getByTestId("explorer-folder-Actors"));
  await ensureOpen(page, side.getByTestId("explorer-folder-People"));
  await expect(side.getByTestId("explorer-row-Clerk")).toContainText("person · persona");

  // New scenario from a process's Scenarios folder (Record from simulation: simulation.mock-only.spec.ts).
  await ensureOpen(page, side.locator('[data-key$="/use:orchestration"]'));
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
  await ensureOpen(page, side.locator('[data-key$="/use:orchestration"]'));
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
  await ensureOpen(page, side.locator('[data-key$="/use:lifecycle"]'));
  await side.getByTestId("explorer-row-InvoiceLifecycle").click({ button: "right" });
  await menuItem(page, "Delete").click();
  await expect(page.getByRole("dialog")).toContainText("Its scenario is deleted with it.");
  await page.getByTestId("confirm-delete").click();
  await expect(side.getByTestId("explorer-row-InvoiceLifecycle")).toHaveCount(0);
  await expect(side.getByTestId("explorer-totals")).toHaveText("2 processes · 3 actors · 2 scenarios");
});

test("searching the Processes explorer finds a process and an actor, and the explorer stays on Processes", async ({ page }) => {
  const side = await openProcesses(page);
  const search = side.getByLabel("Search the model");
  await search.fill("Purchase");
  await expect(side.getByTestId("explorer-row-Purchase approval")).toBeVisible();
  // Its ancestors are open, in the Processes explorer's own grouping.
  await expect(side.locator('[data-key$="/use:orchestration"]')).toHaveAttribute("aria-expanded", "true");
  await search.fill("Approver");
  await expect(side.getByTestId("explorer-row-Approver")).toBeVisible();
  await expect(side.getByTestId("explorer-folder-Roles")).toHaveAttribute("aria-expanded", "true");
  await expect(side.getByTestId("explorer-row-Approver")).toContainText("acts in");
  await search.fill("");
  // Selecting an actor leaves the Processes explorer shown.
  await expect(side.getByTestId("explorer-totals")).toHaveText("2 processes · 3 actors · 3 scenarios");
});

test("a process is revealed in its new use folder after its use changes", async ({ page }) => {
  const side = await openProcesses(page);
  await expand(page, side.getByTestId("explorer-folder-Billing"));
  await ensureOpen(page, side.locator('[data-key$="/use:lifecycle"]'));
  await side.getByTestId("explorer-row-InvoiceLifecycle").dblclick();
  const editor = page.getByTestId("process-editor");
  // The seeded lifecycle states rights, and an orchestration holds none (MQ9901): take them out first.
  await editor.getByRole("tab", { name: "Rights", exact: true }).click();
  const rights = editor.getByTestId("rights-grid-row");
  while ((await rights.count()) > 0)
    await rights
      .first()
      .getByRole("button", { name: /^Remove right/ })
      .click();
  // It also states trigger points, which only a lifecycle holds (MQ8101): take them out too.
  await editor.getByRole("tab", { name: "Trigger points", exact: true }).click();
  const points = editor.getByTestId("trigger-grid-row");
  while ((await points.count()) > 0)
    await points
      .first()
      .getByRole("button", { name: /^Remove trigger point/ })
      .click();
  await editor.getByRole("tab", { name: "States", exact: true }).click();
  await editor.getByTestId("editor-details-toggle").click();
  // The orchestrations folder is not open: changing the use moves the row there, and the selection opens the folder.
  await editor.getByLabel("Use").selectOption("orchestration");
  await expect(side.locator('[data-key$="/use:orchestration"]')).toHaveAttribute("aria-expanded", "true");
  await expect(side.getByTestId("explorer-row-InvoiceLifecycle")).toContainText("orchestration");
});

test("picking a stereotype as the subject updates the lifecycle's explorer row", async ({ page }) => {
  const side = await openProcesses(page);
  await expand(page, side.getByTestId("explorer-folder-Billing"));
  await ensureOpen(page, side.locator('[data-key$="/use:lifecycle"]'));
  const row = side.getByTestId("explorer-row-InvoiceLifecycle");
  await expect(row).not.toContainText("every Audited");
  await row.dblclick();
  const editor = page.getByTestId("process-editor");
  await editor.getByTestId("editor-details-toggle").click();
  await editor.getByLabel("Subject", { exact: true }).selectOption({ label: "«audited»" });
  await expect(side.getByTestId("explorer-row-InvoiceLifecycle")).toContainText("every Audited");
  await expect(side.getByTestId("explorer-row-InvoiceLifecycle")).not.toContainText("Invoice ·");
});
