// Shared Playwright fixtures: every test fails on an uncaught page error or a console error, except
// the browser's own "Failed to load resource" lines for the 4xx answers a test provokes on purpose
// (422 for an invalid save, 409 for a conflict).
import { expect, test as base, type Locator, type Page } from "@playwright/test";

export const test = base.extend<{ errors: string[] }>({
  errors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      page.on("pageerror", (e) => errors.push(`pageerror: ${e.message}`));
      page.on("console", (m) => {
        if (m.type() !== "error") return;
        if (/Failed to load resource: the server responded with a status of 4(09|22)/.test(m.text())) return;
        errors.push(m.text());
      });
      await use(errors);
      expect(errors, "console errors").toEqual([]);
    },
    { auto: true },
  ],
});
export { expect };

/** The React Flow card for an entity (or a table), matched on its exact name. */
export const card = (page: Page, name: string): Locator => page.locator(".react-flow__node").filter({ has: page.getByText(name, { exact: true }) });

/** Opens the editor on the Domain model screen and waits for the default diagram to draw. */
export async function openEditor(page: Page, path = "/"): Promise<void> {
  await page.goto(path);
  await expect(page.getByTestId("shell")).toBeVisible();
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Billing overview");
  await expect(page.locator(".react-flow__node")).toHaveCount(5);
}

export const workspace = (page: Page, name: string): Promise<void> =>
  page.getByRole("navigation", { name: "Explorers" }).getByRole("button", { name, exact: true }).click();
