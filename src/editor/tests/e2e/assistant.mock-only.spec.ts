// The assistant drawer (SPEC Section 14 "Assist", erratum E44) over the mock's scripted assistant: the top-bar button and
// Ctrl+I, a tool row and a proposal card, Apply as one undo step, Discard, and the not-configured state that leads to
// Settings › Assistant. Mock-only: it writes to the mock model.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

/** Whether the mock model has an element with this name. */
const mockHas = (page: Page, name: string): Promise<boolean> =>
  page.evaluate((n) => {
    const mock = (window as unknown as { __mqMock: { model: { index(): { name: string | null }[] } } }).__mqMock;
    return mock.model.index().some((e) => e.name === n);
  }, name);

test("the assistant proposes an entity; Apply creates it in one undo step and Undo removes it", async ({ page }) => {
  await openEditor(page);
  const toggle = page.getByTestId("assistant-toggle");
  await expect(toggle).toHaveAttribute("title", "Assistant (Ctrl+I)");
  await toggle.click();
  const panel = page.getByTestId("assistant-panel");
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId("assistant-input")).toBeFocused();

  // The context chips: the workspace is offered and can be removed before sending.
  await expect(panel.getByTestId("assistant-chip-workspace")).toContainText("Domain model");
  await panel.getByRole("button", { name: "Remove Domain model from the context" }).click();
  await expect(panel.getByTestId("assistant-chip-workspace")).toHaveCount(0);

  await panel.getByTestId("assistant-input").fill("add an entity Foo");
  await panel.getByTestId("assistant-input").press("Enter");
  await expect(panel.getByTestId("assistant-user")).toHaveText("add an entity Foo");
  const tool = panel.getByTestId("assistant-tool");
  await expect(tool).toContainText("get_model_index");
  await expect(tool).toContainText("packages");
  await tool.getByRole("button").click();
  await expect(tool).toContainText('"kind": "package"');

  const card = panel.getByTestId("proposal-card");
  await expect(card).toContainText("Add entity Foo to the billing domain");
  await expect(card.getByTestId("proposal-state")).toHaveText("Waiting for review");
  await expect(card.getByTestId("proposal-file")).toContainText("created");
  await card.getByTestId("proposal-file").click();
  await expect(card.getByTestId("proposal-diff")).toBeVisible();
  await expect(panel.getByTestId("assistant-answer").last()).toContainText("I propose a new entity Foo");
  expect(await mockHas(page, "Foo")).toBe(false);

  await card.getByTestId("proposal-apply").click();
  await expect(card).toHaveAttribute("data-state", "applied");
  await expect(card.getByTestId("proposal-apply")).toHaveCount(0);
  expect(await mockHas(page, "Foo")).toBe(true);
  // The conversation shows in the list, and the proposal's state was recorded.
  await expect(panel.getByTestId("assistant-conversations").locator("option")).toHaveCount(2);

  await page.getByRole("button", { name: "Undo", exact: true }).click();
  await expect.poll(() => mockHas(page, "Foo")).toBe(false);
});

test("Discard leaves the model as it was; Ctrl+I opens and closes the drawer; Clear removes the conversation", async ({ page }) => {
  await openEditor(page);
  await page.locator("body").press("Control+i");
  const panel = page.getByTestId("assistant-panel");
  await expect(panel).toBeVisible();
  await panel.getByTestId("assistant-input").fill("Please add an entity Bar");
  await panel.getByTestId("assistant-send").click();
  const card = panel.getByTestId("proposal-card");
  await expect(card).toBeVisible();
  await card.getByTestId("proposal-discard").click();
  await expect(card).toHaveAttribute("data-state", "discarded");
  expect(await mockHas(page, "Bar")).toBe(false);

  // A plain question gets a short answer in the same conversation.
  await panel.getByTestId("assistant-input").fill("What can you do?");
  await panel.getByTestId("assistant-input").press("Enter");
  await expect(panel.getByTestId("assistant-answer").last()).toContainText("propose changes");

  await panel.getByTestId("assistant-clear").click();
  await expect(panel.getByTestId("assistant-user")).toHaveCount(0);
  await expect(panel.getByTestId("assistant-conversations").locator("option")).toHaveCount(1);

  // Ctrl+I closes it, even from the message field.
  await panel.getByTestId("assistant-input").press("Control+i");
  await expect(panel).toHaveCount(0);
  await page.locator("body").press("Control+i");
  await expect(page.getByTestId("assistant-panel")).toBeVisible();
});

test("with a provider Settings › Assistant names the API it speaks and the model", async ({ page }) => {
  await openEditor(page);
  await page.goto("/settings/assistant");
  const settings = page.getByTestId("assistant-settings");
  await expect(settings.getByTestId("assistant-configured")).toHaveText("Configured");
  await expect(settings.getByTestId("assistant-provider-kind")).toHaveText(" · OpenAI-compatible API");
  await expect(settings.getByTestId("assistant-status")).toContainText("model mock-model");
});

test("without a provider the drawer says so and leads to Settings › Assistant", async ({ page }) => {
  await openEditor(page, "/?mock=noai");
  await page.getByTestId("assistant-toggle").click();
  const panel = page.getByTestId("assistant-panel");
  await expect(panel.getByTestId("assistant-not-configured")).toContainText("The assistant is not configured.");
  await expect(panel.getByTestId("assistant-input")).toHaveCount(0);
  await panel.getByTestId("assistant-open-settings").click();
  await expect(page).toHaveURL(/\/settings\/assistant/);
  const settings = page.getByTestId("assistant-settings");
  await expect(settings.getByTestId("assistant-configured")).toHaveText("Not configured");
  await expect(settings.getByTestId("assistant-provider-kind")).toHaveCount(0);
  await expect(settings.getByRole("link", { name: "Open the host's management UI" })).toHaveAttribute("href", "http://localhost:8090/");

  // The house rules save in maquettiste.json.
  await settings.getByLabel("Instructions").fill("Use singular entity names.");
  await settings.getByLabel("Turns per request").fill("6");
  await settings.getByTestId("save-assistant").click();
  await expect
    .poll(() =>
      page.evaluate(() => (window as unknown as { __mqMock: { model: { settingsJson: Record<string, unknown> } } }).__mqMock.model.settingsJson.assistant),
    )
    .toEqual({ instructions: "Use singular entity names.", maxTurns: 6 });
});
