// The documentation's screenshots (tests/docs/take.sh, `npm run docs:screenshots`): each shot opens one screen of the editor
// over the showcase model (tests/fixtures/models/showcase) and writes docs/images/<name>-light.png and <name>-dark.png. The
// viewport, the data and the theme are fixed, and animations are off, so a refresh changes a picture only when the screen did.
import { expect, test, type Page } from "@playwright/test";
import { fileURLToPath } from "node:url";
import path from "node:path";

const images = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../../docs/images");
const slow = { timeout: 60_000 };

/** Same-origin fetch from the page: Node may not resolve *.localhost, the browser always does. */
async function signIn(page: Page): Promise<void> {
  const token = process.env.MAQUETTISTE_EDITOR_TOKEN;
  if (!token) return;
  await page.goto("/healthz");
  const status = await page.evaluate(async (t) => {
    const res = await fetch("/api/session", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token: t }) });
    return res.status;
  }, token);
  expect(status, "POST /api/session").toBe(200);
}

const rail = (page: Page, name: string) => page.getByRole("navigation", { name: "Explorers" }).getByRole("button", { name, exact: true }).click();
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" }).first();

async function open(page: Page, url = "/"): Promise<void> {
  await page.goto(url);
  await expect(page.getByTestId("shell")).toBeVisible(slow);
}

async function diagram(page: Page, name: string): Promise<void> {
  await page.locator("#diagram-picker").selectOption({ label: name });
  await expect(page.locator(".react-flow__node").first()).toBeVisible(slow);
}

const card = (page: Page, name: string) =>
  page
    .locator(".react-flow__node")
    .filter({ has: page.getByText(name, { exact: true }) })
    .first();

/** Finds an element in the open explorer by its search box, then double-clicks its row (opening it in an editor tab). */
async function openRow(page: Page, name: string): Promise<void> {
  const side = explorer(page);
  await side
    .getByLabel(/^Search/)
    .first()
    .fill(name);
  await side.getByTestId(`explorer-row-${name}`).first().dblclick();
}

interface Shot {
  name: string;
  /** The panels hidden for the picture (mq.layout's collapsed list); by default the bottom panel and the DDL preview. */
  collapsed?: string[];
  take: (page: Page) => Promise<void>;
}

const shots: Shot[] = [
  {
    name: "editor",
    take: async (page) => {
      await open(page, "/entities");
      await diagram(page, "Rentals");
      await card(page, "Rental").click();
    },
  },
  {
    name: "entity-storage",
    collapsed: ["bottom", "ddl", "inspector"],
    take: async (page) => {
      await open(page, "/entities");
      await openRow(page, "Rental");
      const editor = page.getByRole("region", { name: "Editor: Rental" });
      await editor.getByRole("tab", { name: "Storage" }).click();
    },
  },
  {
    name: "process-chart",
    collapsed: ["bottom", "ddl", "inspector"],
    take: async (page) => {
      await open(page, "/entities");
      await rail(page, "Processes");
      const side = explorer(page);
      const folder = side.getByTestId("explorer-folder-Workshop");
      await folder.click();
      await page.keyboard.press("ArrowRight");
      await expect(folder).toHaveAttribute("aria-expanded", "true");
      await side.getByTestId("explorer-row-Repair orchestration").dblclick();
      const editor = page.getByTestId("process-editor");
      await expect(editor.getByRole("tab", { name: "Chart", exact: true })).toHaveAttribute("aria-selected", "true", slow);
      await editor.getByRole("button", { name: "Lay out the whole chart" }).click();
      // A simulation one step in: the work has started, so both regions of In progress are active.
      const panel = editor.getByTestId("simulation-panel");
      const toggle = panel.getByRole("button", { name: /simulation panel/ }).first();
      if ((await toggle.getAttribute("aria-expanded")) !== "true") await toggle.click();
      const start = panel.locator('[data-testid="enabled-row"]').first();
      await expect(start).toBeVisible(slow);
      await start.getByTestId("enabled-raise").click();
      await page.waitForTimeout(1500);
      await editor.locator(".react-flow__controls-fitview").click();
      // The chart runs left to right; zoom in on its parallel state (In progress) rather than show it all at a glance.
      const regions = await Promise.all(
        ["Assessment", "Parts"].map((r) =>
          editor
            .locator(".react-flow__node")
            .filter({ has: page.getByText(r, { exact: true }) })
            .last()
            .boundingBox(),
        ),
      );
      const [first, second] = regions;
      if (first && second) {
        await page.mouse.move((first.x + second.x + second.width) / 2, first.y + first.height / 2);
        for (let i = 0; i < 3; i++) await page.mouse.wheel(0, -100);
        await page.waitForTimeout(600);
      }
    },
  },
  {
    name: "database",
    collapsed: ["bottom", "inspector"],
    take: async (page) => {
      await open(page, "/database");
      await rail(page, "Databases");
      await page.getByRole("button", { name: "Auto-layout" }).click();
      await page.getByText("rentals.rentals", { exact: true }).first().click();
    },
  },
  {
    name: "reference-data",
    take: async (page) => {
      await open(page, "/reference-data");
      await rail(page, "Reference data");
      await page.getByText("Bike type", { exact: true }).last().click();
    },
  },
  {
    name: "generate",
    collapsed: ["ddl", "inspector"],
    take: async (page) => {
      await open(page, "/generate");
      await page.getByTestId("plan").click();
      await expect(page.getByTestId("plan-summary")).toContainText(/sql-ddl: \d+ units?/, slow);
      await page.getByTestId("changes").locator('[data-testid^="change-src/"]').nth(2).click();
      await expect(page.getByTestId("why-panel")).toBeVisible();
    },
  },
  {
    name: "settings-general",
    take: async (page) => {
      await open(page, "/settings/general");
    },
  },
  {
    name: "settings-tags",
    take: async (page) => {
      await open(page, "/settings/tags");
    },
  },
];

for (const shot of shots) {
  for (const scheme of ["light", "dark"] as const) {
    test(`${shot.name} (${scheme})`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
      const collapsed = shot.collapsed ?? ["bottom", "ddl"];
      await page.addInitScript((c) => localStorage.setItem("mq.layout", JSON.stringify({ collapsed: c })), collapsed);
      await signIn(page);
      await shot.take(page);
      // Let the canvas settle (layout, fit, fonts) before the picture.
      await page.evaluate(() => document.fonts.ready);
      await page.waitForTimeout(800);
      await page.screenshot({ path: path.join(images, `${shot.name}-${scheme}.png`), animations: "disabled", caret: "hide" });
    });
  }
}
