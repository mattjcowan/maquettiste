// Settings › General (the owner's 2026-09-29 request): rename the project, upload an SVG icon (its script is removed
// before it is stored), pick a primary color per theme with a contrast warning below 3:1, and see the top bar, the
// tab icon and the accent token follow, live and after the save.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const hex = (s: string) => `#${s}`;
const accent = (page: Page) => page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--mq-accent").trim());
const favicon = (page: Page) => page.evaluate(() => document.getElementById("mq-favicon")?.getAttribute("href") ?? "");

const SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" onload="alert(1)"><script>alert(2)</script><circle cx="8" cy="8" r="6" fill="purple"/></svg>`;

test("rename the project, upload an icon and pick the primary colors", async ({ page }) => {
  await openEditor(page);
  await expect(page.getByTestId("logo")).toHaveText("M");
  await expect.poll(() => favicon(page)).toMatch(/^data:image\/svg\+xml,/); // the M mark by default
  const builtIn = await accent(page);

  await page.getByTestId("rail-settings").click();
  const settings = page.getByTestId("settings-workspace");
  await expect(settings.getByRole("tab", { name: "General" })).toHaveAttribute("aria-selected", "true");
  const save = page.getByTestId("save-general");
  await expect(save).toBeDisabled();

  // The name: the top bar follows as it is typed.
  await settings.getByLabel("Project name").fill("Partner app");
  await expect(page.getByTestId("project-name")).toHaveText("Partner app");

  // The icon: uploaded (the script and the handler are removed), previewed in the top bar and the tab.
  await settings.getByLabel("Upload an icon").setInputFiles({ name: "logo.svg", mimeType: "image/svg+xml", buffer: Buffer.from(SVG) });
  await expect(settings.getByRole("status").filter({ hasText: "Removed from the SVG" })).toContainText("<script>");
  await expect(page.getByTestId("logo")).toHaveAttribute("src", /^data:image\/svg\+xml;base64,/);
  await expect.poll(() => favicon(page)).toMatch(/^data:image\/svg\+xml;base64,/);

  // The colors: a bad value is refused inline, a pale one warns, a good one applies to the tokens.
  const light = settings.getByRole("textbox", { name: "Light theme", exact: true });
  await light.fill("blue");
  await expect(settings.getByText("Use a hex color")).toBeVisible();
  await expect(save).toBeDisabled();
  await light.fill(hex("ffeb3b"));
  await expect(page.getByTestId("contrast-light")).toContainText("below 3:1");
  await light.fill(hex("7a1fa2"));
  await expect(page.getByTestId("contrast-light")).toHaveCount(0);
  await expect.poll(() => accent(page)).toBe(hex("7a1fa2"));
  await settings.getByRole("textbox", { name: "Dark theme", exact: true }).fill(hex("e0a0ff"));

  await save.click();
  await expect(page.getByTestId("notice")).toContainText("General settings saved.");
  await expect(save).toBeDisabled();

  // Saved: the top bar and the tab read the stored icon, the tokens keep the colors in each theme.
  await expect(page.getByTestId("project-name")).toHaveText("Partner app");
  await expect(page.getByTestId("logo")).toHaveAttribute("src", /^\/api\/project\/branding\/icon\?v=[0-9a-f]{12}$/);
  await expect.poll(() => favicon(page)).toMatch(/^\/api\/project\/branding\/icon\?v=/);
  const served = await page.evaluate(async () => {
    const response = await fetch("/api/project/branding/icon");
    return { type: response.headers.get("Content-Type"), text: await response.text() };
  });
  expect(served.type).toBe("image/svg+xml");
  expect(served.text).not.toContain("<script");
  expect(served.text).not.toContain("onload");
  await expect.poll(() => accent(page)).toBe(hex("7a1fa2"));
  await page.evaluate(() => (document.documentElement.dataset.theme = "dark"));
  await expect.poll(() => accent(page)).toBe(hex("e0a0ff"));
  await page.evaluate(() => (document.documentElement.dataset.theme = "light"));

  // Back to the defaults: the M mark and the built-in accent.
  await settings.getByRole("button", { name: "Use the default mark" }).click();
  await settings.getByRole("button", { name: "Reset" }).first().click();
  await settings.getByRole("button", { name: "Reset" }).first().click();
  await save.click();
  await expect(page.getByTestId("logo")).toHaveText("M");
  await expect.poll(() => accent(page)).toBe(builtIn);
  await expect.poll(() => favicon(page)).toMatch(/^data:image\/svg\+xml,/);
});
