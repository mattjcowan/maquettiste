// Shared Playwright fixtures: every test fails on an uncaught page error or a console error, except
// the browser's own "Failed to load resource" lines for the 4xx answers a test provokes on purpose
// (422 for an invalid save, 409 for a conflict). The browser's line names no URL, so the guard also records every answer of
// 400 or more and every request that failed (method, URL, status or reason) and prints them with the console errors.
import { expect, test as base, type Locator, type Page } from "@playwright/test";

export const test = base.extend<{ errors: string[] }>({
  errors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      const requests: string[] = [];
      page.on("pageerror", (e) => errors.push(`pageerror: ${e.message}`));
      page.on("console", (m) => {
        if (m.type() !== "error") return;
        if (/Failed to load resource: the server responded with a status of 4(09|22)/.test(m.text())) return;
        const at = m.location().url;
        errors.push(at && /^Failed to load resource/.test(m.text()) ? `${m.text()} [${at}]` : m.text());
      });
      page.on("response", (r) => {
        if (r.status() >= 400) requests.push(`${r.request().method()} ${r.url()} -> ${r.status()} ${r.statusText()}`.trimEnd());
      });
      page.on("requestfailed", (r) => requests.push(`${r.method()} ${r.url()} failed: ${r.failure()?.errorText ?? "unknown"}`));
      await use(errors);
      const detail = requests.length ? `\nfailed requests (in order):\n  ${requests.join("\n  ")}` : "";
      expect(errors, `console errors${detail}`).toEqual([]);
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

/**
 * The Database screen opens with its DDL preview hidden behind its edge; the specs that read the preview start as someone who
 * opened it once (the saved layout lists no hidden panel). Only when nothing is saved yet, so a reload keeps what the test did.
 */
export async function keepDdlOpen(page: Page): Promise<void> {
  await page.addInitScript(() => {
    try {
      if (!localStorage.getItem("mq.layout")) localStorage.setItem("mq.layout", JSON.stringify({ collapsed: [] }));
    } catch {
      // about:blank has no storage.
    }
  });
}

/** Opens the Database screen's DDL preview from its edge, as a person would. */
export async function showDdl(page: Page): Promise<void> {
  await page.getByTestId("show-ddl").click();
  await expect(page.getByTestId("ddl-preview")).toBeVisible();
}
