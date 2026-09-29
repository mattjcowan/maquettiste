// Shell (mock project): the theme switch, one dense row height (no density toggle), the command palette, and an axe scan of each
// workspace in both themes (no serious or critical violations).
import AxeBuilder from "@axe-core/playwright";
import { expect, openEditor, test, workspace } from "./fixtures";

test("theme, one density and the command palette", async ({ page }) => {
  await openEditor(page);
  const html = page.locator("html");
  await page.getByTestId("theme-menu").click();
  await page.getByRole("menuitemradio", { name: "Dark" }).click();
  await expect(html).toHaveAttribute("data-theme", "dark");
  await page.getByTestId("theme-menu").click();
  await page.getByRole("menuitemradio", { name: "Light" }).click();
  await expect(html).toHaveAttribute("data-theme", "light");

  // One density (E23): no toggle, no preference, 24 px rows for a fine pointer.
  await expect(page.getByTestId("density-toggle")).toHaveCount(0);
  await expect(html).not.toHaveAttribute("data-density", /.*/);
  expect(await html.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--mq-row-h").trim())).toBe("24px");

  await page.keyboard.press("Control+k");
  const palette = page.getByTestId("command-palette");
  await expect(palette).toBeVisible();
  await page.keyboard.type("Payment");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("region", { name: "Inspector: Payment" })).toBeVisible();

  // Palette commands run, not just navigate: New entity opens the dialog, Plan starts a plan.
  await page.keyboard.press("Control+k");
  await page.keyboard.type("New entity");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("dialog", { name: "New entity" })).toBeVisible();
  await page.keyboard.press("Escape");
  await page.keyboard.press("Control+k");
  await page.keyboard.type("Plan generation");
  await page.keyboard.press("Enter");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await expect(page.getByRole("button", { name: "Apply plan" })).toBeEnabled({ timeout: 15_000 });
});

test("keyboard: every stop on a Tab walk shows a focus indicator", async ({ page }) => {
  await openEditor(page);
  await page.locator("body").click({ position: { x: 1, y: 1 } });
  const bare: string[] = [];
  let separators = 0;
  for (let i = 0; i < 80; i++) {
    await page.keyboard.press("Tab");
    const stop = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      if (!el || el === document.body) return null;
      const style = getComputedStyle(el);
      const edge = el.classList.contains("react-flow__edge");
      const stroke = edge ? getComputedStyle(el.querySelector("path") ?? el).strokeWidth : "";
      const visible = (style.outlineStyle !== "none" && parseFloat(style.outlineWidth) >= 2) || style.boxShadow !== "none" || (edge && parseFloat(stroke) >= 3);
      return {
        name: `${el.getAttribute("role") ?? el.tagName} ${el.getAttribute("aria-label") ?? el.textContent?.slice(0, 30) ?? ""}`,
        visible,
        separator: el.getAttribute("role") === "separator",
      };
    });
    if (!stop) continue;
    if (stop.separator) separators++;
    if (!stop.visible) bare.push(stop.name);
  }
  expect(separators).toBeGreaterThan(0);
  expect(bare).toEqual([]);
});

for (const theme of ["light", "dark"] as const) {
  test(`axe: every workspace in the ${theme} theme`, async ({ page }) => {
    // Reduced motion sets --mq-motion to 0 ms, so axe never measures a control halfway through its
    // opacity transition (a button that has just become enabled reads as low contrast).
    await page.emulateMedia({ reducedMotion: "reduce" });
    await page.addInitScript((t) => localStorage.setItem("mq.theme", t), theme);
    await openEditor(page);
    // The rail's explorers and screens, then Mappings (opened from a database's menu or the palette).
    for (const name of ["Domain model", "Reference data", "Databases", "Diagrams", "Generate", "Settings", "Mappings"]) {
      if (name === "Mappings") {
        await page.keyboard.press("Control+k");
        await page.keyboard.type("screen Mappings");
        await page.keyboard.press("Enter");
        await expect(page.getByTestId("workspace-mappings")).toBeVisible();
      } else await workspace(page, name);
      await expect(page.getByRole("main")).toBeVisible();
      await page.waitForLoadState("networkidle");
      const scan = await new AxeBuilder({ page }).exclude(".react-flow__minimap").analyze();
      const bad = scan.violations
        .filter((v) => v.impact === "serious" || v.impact === "critical")
        .map((v) => `${name}: ${v.id} (${v.nodes.length}) ${v.nodes[0]?.target.join(" ")}`);
      expect(bad, `${theme} ${name}`).toEqual([]);
    }
  });
}
