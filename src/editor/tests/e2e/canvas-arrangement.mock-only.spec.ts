// What the user arranges on a canvas stays arranged (canvas/placement.ts, canvas/domainDiagram.ts): pan and zoom are
// saved in the diagram and restored on open; a card that arrives later is placed beside its related card without moving
// the others; and the first arrangement of "All of <domain>" creates that domain's own diagram.
import type { Page } from "@playwright/test";
import { card, expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const PAYMENT = "01J92P0V0HEGSC6MW92CST5KA6";
const OVERVIEW = "01J92P0V2164SDBW687ZV6E1MV";
const ulid = (n: number) => `01J92P0VZZ${String(n).padStart(16, "0")}`;

type Box = { x: number; y: number; width: number; height: number };
type Diagram = {
  name: string;
  package?: string;
  membership?: "explicit" | "package";
  members: { element: string; x?: number; y?: number }[];
  viewport?: { x: number; y: number; zoom: number };
};

const zoomOf = async (page: Page) => Number(/scale\(([\d.]+)\)/.exec((await page.locator(".react-flow__viewport").getAttribute("style")) ?? "")?.[1]);

/** The domain's own diagram (membership "package"), read from the mock server (null while there is none). */
const domainDiagram = (page: Page) =>
  page.evaluate(async (pkg) => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { elements?: { id: string; kind: string; name: string; package?: string }[] } | { id: string; kind: string; name: string; package?: string }[];
    const rows = Array.isArray(index) ? index : (index.elements ?? []);
    for (const row of rows.filter((r) => r.kind === "diagram" && r.package === pkg)) {
      const json = ((await (await fetch(`/api/model/elements/${row.id}`)).json()) as { json: Diagram }).json;
      if (json.membership === "package") return json;
    }
    return null;
  }, BILLING);

async function boxOf(page: Page, name: string): Promise<Box> {
  const box = await card(page, name).boundingBox();
  expect(box, name).not.toBeNull();
  return box!;
}

test("zoom is saved in the diagram and restored on the way back; viewing a domain creates nothing", async ({ page }) => {
  await openEditor(page);
  await page.waitForTimeout(600); // past the canvas's own fit on open
  const before = await zoomOf(page);
  await page.getByRole("button", { name: "Zoom In" }).click();
  await expect.poll(() => zoomOf(page)).toBeGreaterThan(before);
  const zoomed = await zoomOf(page);
  // Saved in the diagram's file, rounded to three decimals.
  await expect
    .poll(() =>
      page.evaluate(async (id) => ((await (await fetch(`/api/model/elements/${id}`)).json()) as { json: Diagram }).json.viewport?.zoom ?? null, OVERVIEW),
    )
    .toBeCloseTo(zoomed, 3);

  await page.locator("#diagram-picker").selectOption({ label: "All of Billing" });
  await expect(page.locator(".react-flow__node")).toHaveCount(4);
  await page.waitForTimeout(600);
  expect(await domainDiagram(page)).toBeNull();

  await page.locator("#diagram-picker").selectOption({ label: "Billing overview" });
  await expect(page.locator(".react-flow__node")).toHaveCount(5);
  await expect.poll(() => zoomOf(page)).toBeCloseTo(zoomed, 2);
});

test("arranging All of Billing creates the domain's diagram; a new related entity is placed beside its relation and nothing moves", async ({ page }) => {
  await openEditor(page);
  await page.locator("#diagram-picker").selectOption({ label: "All of Billing" });
  await expect(page.locator(".react-flow__node")).toHaveCount(4);
  await page.waitForTimeout(600);

  // Drag Payment down: the first arrangement creates the diagram "Billing" and the canvas becomes it.
  const start = await boxOf(page, "Payment");
  await page.mouse.move(start.x + start.width / 2, start.y + 12);
  await page.mouse.down();
  await page.mouse.move(start.x + start.width / 2, start.y + 72, { steps: 6 });
  await page.mouse.move(start.x + start.width / 2, start.y + 132, { steps: 6 });
  await page.mouse.up();
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Billing");
  const created = await domainDiagram(page);
  expect(created).not.toBeNull();
  const entityMembers = created!.members.filter((m) => m.x !== undefined);
  expect(entityMembers).toHaveLength(4);
  expect(created!.members).toHaveLength(7); // four entities and the three relationships between them
  expect(created!.viewport).toEqual({ x: expect.any(Number), y: expect.any(Number), zoom: expect.any(Number) });
  await page.waitForTimeout(600);
  const dragged = await boxOf(page, "Payment");
  expect(dragged.y).toBeGreaterThan(start.y + 60);

  // A new entity in the domain, related to Payment, saved elsewhere (the server tells the editor).
  const saved = await page.evaluate(
    async ({ pkg, payment, ids }) => {
      const res = await fetch("/api/model/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          operations: [
            {
              op: "create",
              element: {
                kind: "entity",
                id: ids[0],
                name: "Refund",
                package: pkg,
                key: { attributes: [ids[1]], strategy: "uuid-v7" },
                attributes: [{ id: ids[1], name: "id", type: "uuid", required: true }],
              },
            },
            {
              op: "create",
              element: {
                kind: "relation",
                id: ids[2],
                name: "refunds",
                package: pkg,
                ends: [
                  { id: ids[3], entity: ids[0], role: "refund", navigation: "refund" },
                  { id: ids[4], entity: payment, role: "payment", navigation: "payment", min: 1, max: 1 },
                ],
              },
            },
          ],
        }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    { pkg: BILLING, payment: PAYMENT, ids: [1, 2, 3, 4, 5].map(ulid) },
  );
  expect(saved).toBe("saved");
  await expect(card(page, "Refund")).toBeVisible();
  await expect(page.locator(".react-flow__node")).toHaveCount(5);
  await expect(page.getByTestId("save-status")).toHaveText("Saved");

  // Payment kept its place; Refund sits beside it and overlaps no card.
  const payment = await boxOf(page, "Payment");
  expect(Math.abs(payment.x - dragged.x)).toBeLessThan(1);
  expect(Math.abs(payment.y - dragged.y)).toBeLessThan(1);
  const refund = await boxOf(page, "Refund");
  const gapX = Math.max(refund.x - (payment.x + payment.width), payment.x - (refund.x + refund.width), 0);
  const gapY = Math.max(refund.y - (payment.y + payment.height), payment.y - (refund.y + refund.height), 0);
  expect(gapX + gapY).toBeGreaterThan(0);
  expect(gapX + gapY).toBeLessThan(120);
  for (const name of ["Customer", "Invoice", "InvoiceLine", "Payment"]) {
    const other = await boxOf(page, name);
    const overlap =
      refund.x < other.x + other.width && other.x < refund.x + refund.width && refund.y < other.y + other.height && other.y < refund.y + refund.height;
    expect(overlap, `Refund overlaps ${name}`).toBe(false);
  }
  // The diagram gained Refund and its relationship, with a position.
  await expect.poll(async () => (await domainDiagram(page))?.members.find((m) => m.element === ulid(1))?.x !== undefined).toBe(true);
  expect((await domainDiagram(page))!.members.some((m) => m.element === ulid(3))).toBe(true);
});

test("positions the browser kept for a domain's canvas move into the domain's diagram once", async ({ page }) => {
  await page.addInitScript(
    ({ pkg, payment }) => {
      if (sessionStorage.getItem("seeded")) return;
      sessionStorage.setItem("seeded", "1");
      localStorage.setItem(`mq.pos.pkg.${pkg}`, JSON.stringify({ [payment]: { x: 1000, y: 700 } }));
    },
    { pkg: BILLING, payment: PAYMENT },
  );
  await openEditor(page);
  await page.locator("#diagram-picker").selectOption({ label: "All of Billing" });
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Billing");
  await expect(page.locator(".react-flow__node")).toHaveCount(4);
  const created = await domainDiagram(page);
  expect(created!.members.find((m) => m.element === PAYMENT)).toMatchObject({ x: 1000, y: 700 });
  // The others were placed beside it and saved; the browser's copy is gone.
  await expect.poll(async () => (await domainDiagram(page))?.members.filter((m) => m.x !== undefined).length).toBe(4);
  expect(await page.evaluate((pkg) => localStorage.getItem(`mq.pos.pkg.${pkg}`), BILLING)).toBeNull();
});

test("Auto-layout on All of Billing creates the domain's diagram; Undo removes it and the canvas is All of Billing again", async ({ page }) => {
  await openEditor(page);
  await page.locator("#diagram-picker").selectOption({ label: "All of Billing" });
  await expect(page.locator(".react-flow__node")).toHaveCount(4);
  await page.getByTestId("auto-layout").click();
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Billing");
  const created = await domainDiagram(page);
  expect(created!.members.filter((m) => m.x !== undefined)).toHaveLength(4);
  expect(created!.membership).toBe("package");

  await page.getByRole("button", { name: "Undo" }).click();
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("All of Billing");
  await expect.poll(() => domainDiagram(page)).toBeNull();
  await expect(page.locator(".react-flow__node")).toHaveCount(4);
});

test("the domain's diagram is found by its membership, not its name: renamed, All of Billing still opens it", async ({ page }) => {
  await openEditor(page);
  await page.locator("#diagram-picker").selectOption({ label: "All of Billing" });
  await expect(page.locator(".react-flow__node")).toHaveCount(4);
  await page.getByTestId("auto-layout").click();
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Billing");

  // The diagram's inspector shows the membership, read-only; renaming the diagram changes nothing.
  const tree = page.getByRole("tree", { name: "Diagrams" });
  const openDiagramRow = async (name: RegExp) => {
    await page.getByTestId("rail-diagrams").click();
    const domain = tree.getByRole("treeitem", { level: 1 }).filter({ hasText: "Billing" }).first();
    if ((await domain.textContent())?.includes("collapsed")) await domain.locator("span[aria-hidden]").first().click();
    // A Ctrl+click selects the diagram (a plain click opens it) so the inspector shows it.
    await tree
      .getByRole("treeitem", { level: 2, name })
      .first()
      .click({ modifiers: ["ControlOrMeta"] });
  };
  await openDiagramRow(/^Billing(?! overview)/);
  const inspector = page.getByRole("complementary", { name: "Inspector" });
  const membership = inspector.getByTestId("diagram-membership");
  await expect(membership).toHaveValue("Follows the domain");
  await expect(membership).toHaveAttribute("readonly", "");
  await inspector.getByLabel("Name", { exact: true }).fill("Receivables canvas");
  await inspector.getByLabel("Name", { exact: true }).press("Tab");
  await expect.poll(async () => (await domainDiagram(page))?.name).toBe("Receivables canvas");

  await page.getByTestId("rail-domain-model").click();
  await page.locator("#diagram-picker").selectOption({ label: "Billing overview" });
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Billing overview");
  await page.locator("#diagram-picker").selectOption({ label: "All of Billing" });
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Receivables canvas");

  // An ordinary diagram shows explicit members (the Ctrl+click first takes the renamed diagram out of the selection).
  await openDiagramRow(/^Receivables canvas/);
  await openDiagramRow(/^Billing overview/);
  await expect(membership).toHaveValue("Explicit members");
});
