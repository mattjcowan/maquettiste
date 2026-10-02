// The statechart canvas (phase-3-design.md 6.3, 2.7): the process editor opens on Chart with the containers and the
// parallel regions drawn at the places the process diagram saves; selecting a state or an edge shows its inspector
// section; N places a new state inside its container and nothing else moves; Layout creates a chart's diagram the
// first time (the Diagrams explorer lists it and opens it on the Chart tab); a reload draws the saved places again; the
// keyboard walks the chart; a delete asks when transitions enter the state; F12 on an edge opens its gate or event.
// Mock-only: PurchaseApproval's diagram lives in mocks/model/processDiagramSeed.ts.
import AxeBuilder from "@axe-core/playwright";
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const PURCHASE_APPROVAL = "01JQPRC0000000000000000002";
const PURCHASE_DIAGRAM = "01JQDGM0000000000000000001";
const INVOICE_LIFECYCLE = "01JQMCK0000000000000000001";
const S = (n: number) => `01JQSTA0000000000000000${String(n).padStart(3, "0")}`;

type Member = { element: string; x?: number; y?: number; width?: number; height?: number; collapsed?: boolean };
type Diagram = { id: string; name: string; package?: string; process?: string; members: Member[] };

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const chart = (page: Page) => page.getByTestId("statechart-canvas");
const state = (page: Page, name: string) => chart(page).getByTestId(`state-${name}`);
const label = (page: Page, text: string) =>
  chart(page)
    .getByTestId("transition-label")
    .filter({ hasText: new RegExp(`^${text}`) });
const tab = (editor: Locator, name: string) => editor.getByRole("tab", { name, exact: true });

/** Opens a row of the Billing folder of an explorer (Processes or Diagrams) with a double click. */
async function openRow(page: Page, place: "Processes" | "Diagrams", row: string) {
  await workspace(page, place);
  const side = explorer(page);
  const billing = side.getByTestId("explorer-folder-Billing");
  if ((await billing.getAttribute("aria-expanded")) !== "true") {
    await billing.click();
    await page.keyboard.press("ArrowRight");
    await expect(billing).toHaveAttribute("aria-expanded", "true");
  }
  await side.getByTestId(`explorer-row-${row}`).dblclick();
  const editor = page.getByTestId("process-editor");
  await expect(editor).toBeVisible();
  await expect(chart(page)).toBeVisible();
  return editor;
}

async function openProcess(page: Page, row: string, path = "/") {
  await openEditor(page, path);
  return openRow(page, "Processes", row);
}

/** A document as the mock server has it now. */
const serverJson = <T>(page: Page, id: string) =>
  page.evaluate(async (x) => {
    const response = await fetch(`/api/model/elements/${x}`);
    return response.ok ? (((await response.json()) as { json: unknown }).json as T) : null;
  }, id) as Promise<T | null>;

/** The diagram whose index row names the process (null while there is none). */
const diagramOf = (page: Page, process: string) =>
  page.evaluate(async (p) => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { elements?: { id: string; kind: string; process?: string }[] } | { id: string; kind: string; process?: string }[];
    const rows = Array.isArray(index) ? index : (index.elements ?? []);
    const row = rows.find((r) => r.kind === "diagram" && r.process === p);
    return row ? (((await (await fetch(`/api/model/elements/${row.id}`)).json()) as { json: unknown }).json as Diagram) : null;
  }, process);

const positions = (d: Diagram | null) => Object.fromEntries((d?.members ?? []).map((m) => [m.element, [m.x, m.y]]));

/** A node's place on the canvas (React Flow draws it at its absolute flow position). */
async function placeOf(node: Locator): Promise<[number, number]> {
  const style = (await node.locator("xpath=ancestor::div[contains(@class,'react-flow__node')][1]").getAttribute("style")) ?? "";
  const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(style);
  return [Number(m?.[1]), Number(m?.[2])];
}

test("PurchaseApproval opens on its chart: containers, parallel regions, labels, the gate badge, and the inspector follows", async ({ page }) => {
  const editor = await openProcess(page, "Purchase approval");
  await expect(tab(editor, "Chart")).toHaveAttribute("aria-selected", "true");
  await expect(chart(page)).toHaveAttribute("data-diagram", PURCHASE_DIAGRAM);
  await expect(state(page, "Review")).toHaveAttribute("data-type", "parallel");
  await expect(state(page, "Review").getByTestId("region-separator")).toHaveCount(1);
  for (const name of ["Budget", "Compliance", "Checking", "Pending", "Approval", "Ordered"]) await expect(state(page, name)).toBeVisible();
  await expect(state(page, "Ordered")).toHaveAttribute("data-type", "final");
  // Saved places: Drafting at (40, 100); Checking inside Budget inside Review.
  expect(await placeOf(state(page, "Drafting"))).toEqual([40, 100]);
  expect(await placeOf(state(page, "Checking"))).toEqual([190 + 12 + 48, 20 + 32 + 32]);
  // Labels: an event, an invoke's done, the timer inside its state; the gate badge names its signers.
  await expect(label(page, "submit")).toBeVisible();
  await expect(label(page, "done: checkBudget")).toBeVisible();
  await expect(state(page, "Approval").getByTestId("internal-transition")).toHaveText("after 5d / sendReminder");
  const gate = label(page, "approve").getByTestId("gate-badge");
  await expect(gate).toHaveText("2 of 1");
  await expect(gate).toHaveAttribute("title", /Approver/);

  await state(page, "Checking").click();
  await expect(page.getByTestId("inspector-state")).toContainText("Checking");
  await label(page, "approve").click();
  await expect(page.getByTestId("inspector-transition")).toBeVisible();
  await expect(chart(page)).toHaveAttribute("data-selected", "01JQTRN0000000000000000021");
  await chart(page)
    .locator(".react-flow__pane")
    .click({ position: { x: 5, y: 5 } });
  await expect(page.getByTestId("inspector-process")).toBeVisible();
});

test("N places a new state and nothing else moves; the add is one undo step; a rename and a reload leave saved places alone", async ({ page }) => {
  await openProcess(page, "Purchase approval");
  const before = await diagramOf(page, PURCHASE_APPROVAL);
  expect(before?.members).toHaveLength(13);

  await state(page, "Checking").click();
  await page.keyboard.press("n");
  const added = state(page, "State");
  await expect(added).toBeVisible();
  // Saved in the diagram with the new member; every saved state keeps its place (its containers may only grow).
  await expect.poll(async () => (await diagramOf(page, PURCHASE_APPROVAL))?.members.length).toBe(14);
  const after = await diagramOf(page, PURCHASE_APPROVAL);
  const was = positions(before);
  for (const [id, place] of Object.entries(positions(after))) if (was[id]) expect(place, id).toEqual(was[id]);
  const fresh = after!.members.find((m) => !was[m.element])!;
  // Inside Budget, under Checking's row: placed in its container only.
  expect(fresh.x).toBeGreaterThanOrEqual(12);
  expect(fresh.y).toBeGreaterThan(32);
  await expect(page.getByTestId("inspector-state")).toContainText("State");

  // One undo step takes back the state and its place.
  await page.keyboard.press("ControlOrMeta+z");
  await expect(added).toHaveCount(0);
  await expect.poll(async () => (await diagramOf(page, PURCHASE_APPROVAL))?.members.length).toBe(13);

  // F2 renames in place and moves nothing.
  const names = ["Drafting", "Checking", "BudgetOk", "Approval"];
  const seeded = await Promise.all(names.map((n) => placeOf(state(page, n))));
  await state(page, "Checking").click();
  await page.keyboard.press("F2");
  await chart(page).getByTestId("state-rename").fill("Verifying");
  await page.keyboard.press("Enter");
  await expect(state(page, "Verifying")).toBeVisible();
  await expect.poll(async () => JSON.stringify(await serverJson(page, PURCHASE_APPROVAL))).toContain('"Verifying"');
  expect(positions(await diagramOf(page, PURCHASE_APPROVAL))).toEqual(was);
  expect(await Promise.all(["Drafting", "Verifying", "BudgetOk", "Approval"].map((n) => placeOf(state(page, n))))).toEqual(seeded);

  // A reload (the mock model starts again from its seed) draws every state at its saved place.
  await page.reload();
  await expect(page.getByTestId("shell")).toBeVisible();
  await openRow(page, "Processes", "Purchase approval");
  await expect(state(page, "Checking")).toBeVisible();
  expect(await Promise.all(names.map((n) => placeOf(state(page, n))))).toEqual(seeded);
});

test("Layout creates InvoiceLifecycle's diagram; the Diagrams explorer lists it and opens it on the Chart tab at the same places", async ({ page }) => {
  const editor = await openProcess(page, "InvoiceLifecycle");
  // Looking at a chart creates nothing: it is drawn from a layout held in memory.
  await expect(state(page, "Draft")).toBeVisible();
  await expect(chart(page).locator("xpath=..").getByTestId("chart-not-saved")).toBeVisible();
  await expect(state(page, "Draft").getByTestId("state-bound-member")).toHaveText("Draft");
  await page.waitForTimeout(600);
  expect(await diagramOf(page, INVOICE_LIFECYCLE)).toBeNull();

  await editor.getByTestId("chart-layout").click();
  await expect(chart(page)).toHaveAttribute("data-diagram", /.+/);
  await expect(chart(page)).toHaveAttribute("data-layout-ms", /\d+/);
  const created = await diagramOf(page, INVOICE_LIFECYCLE);
  expect(created).toMatchObject({ name: "InvoiceLifecycle", package: "01J92P0V01KDRN8GX5PGYCNKSX", process: INVOICE_LIFECYCLE });
  expect(created!.members).toHaveLength(4);
  const names = ["Draft", "Issued", "Paid", "Void"];
  const places = await Promise.all(names.map((n) => placeOf(state(page, n))));
  expect(places).toEqual(created!.members.map((m) => [m.x, m.y]));

  await workspace(page, "Diagrams");
  const side = explorer(page);
  const folder = side.getByTestId("explorer-folder-Billing");
  if ((await folder.getAttribute("aria-expanded")) !== "true") {
    await folder.click();
    await page.keyboard.press("ArrowRight");
  }
  await expect(side.getByTestId("explorer-row-InvoiceLifecycle")).toContainText("statechart of InvoiceLifecycle");
  await expect(side.getByTestId("explorer-row-Purchase approval")).toContainText("statechart of Purchase approval");

  // The Domain model's diagram picker leaves process diagrams out.
  await page.getByTestId("editor-tab-screen").click();
  await expect(page.locator("#diagram-picker option", { hasText: "InvoiceLifecycle" })).toHaveCount(0);
  await expect(page.locator("#diagram-picker option", { hasText: "Billing overview" })).toHaveCount(1);
  // Opening the diagram's row opens the process editor on its Chart tab, the states where the layout put them.
  await openRow(page, "Diagrams", "InvoiceLifecycle");
  await expect(page.getByTestId("process-editor")).toHaveAttribute("data-id", INVOICE_LIFECYCLE);
  await expect(tab(page.getByTestId("process-editor"), "Chart")).toHaveAttribute("aria-selected", "true");
  await expect(state(page, "Paid")).toBeVisible();
  const reopened = await Promise.all(names.map((n) => placeOf(state(page, n))));
  expect(reopened).toEqual(places);
});

test("the keyboard walks the chart: arrows, Enter into a container, Escape out, Tab over the outgoing transitions", async ({ page }) => {
  await openProcess(page, "Purchase approval");
  await chart(page).focus();
  const selected = () => chart(page).getAttribute("data-selected");
  await page.keyboard.press("ArrowRight");
  await expect.poll(selected).toBe(S(101)); // nothing selected: the initial state, Drafting
  await page.keyboard.press("ArrowRight");
  await expect.poll(selected).toBe(S(102)); // Review
  await page.keyboard.press("Enter");
  await expect.poll(selected).toBe(S(103)); // its first region, Budget
  await page.keyboard.press("ArrowRight");
  await expect.poll(selected).toBe(S(107)); // the region beside it, Compliance
  await page.keyboard.press("ArrowLeft");
  await page.keyboard.press("Enter");
  await expect.poll(selected).toBe(S(104)); // Budget's initial state, Checking
  await page.keyboard.press("ArrowRight");
  await expect.poll(selected).toBe(S(105)); // BudgetOk
  await expect(page.getByTestId("inspector-state")).toContainText("BudgetOk");
  await page.keyboard.press("Escape");
  await expect.poll(selected).toBe(S(103));
  await page.keyboard.press("Escape");
  await expect.poll(selected).toBe(S(102));
  await page.keyboard.press("ArrowRight");
  await expect.poll(selected).toBe(S(110)); // Approval
  // Tab walks Approval's outgoing transitions in document order: approve, reject, after 5d.
  await page.keyboard.press("Tab");
  await expect.poll(selected).toBe("01JQTRN0000000000000000021");
  await expect(page.getByTestId("inspector-transition")).toBeVisible();
  await page.keyboard.press("Tab");
  await expect.poll(selected).toBe("01JQTRN0000000000000000022");
  await page.keyboard.press("Tab");
  await expect.poll(selected).toBe("01JQTRN0000000000000000023");
  await page.keyboard.press("Shift+Tab");
  await expect.poll(selected).toBe("01JQTRN0000000000000000022");
  // Escape from a transition selects its source; again at the root selects nothing (the process).
  await page.keyboard.press("Escape");
  await expect.poll(selected).toBe(S(110));
  await page.keyboard.press("Escape");
  await expect.poll(selected).toBeNull();
  await expect(page.getByTestId("inspector-process")).toBeVisible();

  // Assistive technology follows the selection: the focused canvas names the selected state (aria-activedescendant),
  // the state says it is selected, and a polite live region announces it.
  await page.keyboard.press("ArrowRight");
  await expect.poll(selected).toBe(S(101));
  await expect(chart(page)).toHaveAttribute("aria-activedescendant", `mq-chart-state-${S(101)}`);
  await expect(state(page, "Drafting")).toHaveAttribute("aria-selected", "true");
  await expect(state(page, "Approval")).toHaveAttribute("aria-selected", "false");
  await expect(chart(page).getByTestId("chart-announcement")).toHaveText("Selected state Drafting");
  await page.keyboard.press("Tab");
  await expect(chart(page)).toHaveAttribute("aria-activedescendant", /^mq-chart-edge-01JQTRN0000000000000000015/);
  await expect(chart(page).getByTestId("chart-announcement")).toHaveText("Selected transition submit");
  expect((await new AxeBuilder({ page }).include('[data-testid="statechart"]').analyze()).violations).toEqual([]);
  await page.keyboard.press("Escape");
  await page.keyboard.press("Escape");
  await expect.poll(selected).toBeNull();
  await expect(chart(page)).not.toHaveAttribute("aria-activedescendant", /.+/);

  // T draws a transition: arrows pick the target, Enter confirms (on the first event, submit).
  await page.keyboard.press("ArrowRight");
  await expect.poll(selected).toBe(S(101));
  const labels = chart(page).getByTestId("transition-label");
  // The labels are drawn again after the selection moves: count them once they are back.
  await expect(labels).not.toHaveCount(0);
  const count = await labels.count();
  await page.keyboard.press("t");
  await expect(chart(page).locator("xpath=..").getByTestId("chart-hint")).toContainText("New transition from Drafting");
  // The nearest state to the right, among every state drawn: Checking, inside Review.
  await page.keyboard.press("ArrowRight");
  await expect(state(page, "Checking")).toHaveAttribute("data-link-target", "true");
  await page.keyboard.press("Enter");
  await expect(labels).toHaveCount(count + 1);
  await expect(page.getByTestId("inspector-transition")).toBeVisible();
});

test("the mouse draws a transition: a click picks the target in T mode, and a state's handle drags onto the target", async ({ page }) => {
  await openProcess(page, "Purchase approval");
  const labels = chart(page).getByTestId("transition-label");
  const count = await labels.count();
  const hint = chart(page).locator("xpath=..").getByTestId("chart-hint");

  // T, then a click on the target.
  await state(page, "Drafting").getByTitle("Drafting", { exact: true }).click();
  await page.keyboard.press("t");
  await expect(hint).toContainText("New transition from Drafting");
  await state(page, "Approval").hover();
  await expect(state(page, "Approval")).toHaveAttribute("data-link-target", "true");
  await state(page, "Approval").getByTitle("Approval", { exact: true }).click();
  await expect(labels).toHaveCount(count + 1);
  await expect(page.getByTestId("inspector-transition")).toBeVisible();
  await expect(hint).not.toContainText("New transition");

  // The handle on a hovered state drags onto the target; a drop on the canvas draws nothing.
  const handle = state(page, "Drafting").getByTestId("chart-link-handle");
  await expect(handle).toHaveAttribute("title", "Draw a transition from Drafting (drag onto the target state)");
  const drag = async (to: { x: number; y: number }) => {
    await state(page, "Drafting").hover();
    const from = (await handle.boundingBox())!;
    await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
    await page.mouse.down();
    await page.mouse.move(from.x + 40, from.y + 10, { steps: 4 });
    await page.mouse.move(to.x, to.y, { steps: 8 });
    await page.mouse.up();
  };
  const checking = (await state(page, "Checking").boundingBox())!;
  await drag({ x: checking.x + checking.width / 2, y: checking.y + 10 });
  await expect(labels).toHaveCount(count + 2);
  await expect(page.getByTestId("inspector-transition")).toBeVisible();
  const pane = (await chart(page).boundingBox())!;
  await drag({ x: pane.x + pane.width - 8, y: pane.y + pane.height - 8 });
  await expect(labels).toHaveCount(count + 2);
  await expect(hint).not.toContainText("New transition");
  // Two gestures, two undo steps.
  await page.keyboard.press("ControlOrMeta+z");
  await expect(labels).toHaveCount(count + 1);
  await page.keyboard.press("ControlOrMeta+z");
  await expect(labels).toHaveCount(count);
});

test("Delete asks when transitions enter the state, lists them, and the delete is one undo step with the diagram", async ({ page }) => {
  await openProcess(page, "Purchase approval");
  // On its name (the row under it is its timer, an internal transition).
  await state(page, "Approval").getByTitle("Approval", { exact: true }).click();
  await page.keyboard.press("Delete");
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("Delete Approval?");
  await expect(dialog.getByTestId("delete-incoming")).toContainText("Review · done → Approval");
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(state(page, "Approval")).toBeVisible();

  await state(page, "Approval").getByTitle("Approval", { exact: true }).click();
  await page.keyboard.press("Delete");
  await page.getByTestId("confirm-delete-state").click();
  await expect(state(page, "Approval")).toHaveCount(0);
  await expect.poll(async () => (await diagramOf(page, PURCHASE_APPROVAL))?.members.some((m) => m.element === S(110))).toBe(false);
  const process = await serverJson<{ transitions: { source: string; targets?: string[] }[] }>(page, PURCHASE_APPROVAL);
  expect(process!.transitions.some((t) => t.source === S(110) || t.targets?.includes(S(110)))).toBe(false);

  await page.keyboard.press("ControlOrMeta+z");
  await expect(state(page, "Approval")).toBeVisible();
  await expect.poll(async () => (await diagramOf(page, PURCHASE_APPROVAL))?.members.find((m) => m.element === S(110))).toMatchObject({ x: 1070, y: 90 });

  // Shift+click selects several states; Delete deletes them together, asking once for every transition entering them.
  await state(page, "Ordered").click();
  await state(page, "Rejected").click({ modifiers: ["Shift"] });
  await expect(state(page, "Ordered")).toHaveAttribute("data-selected", "true");
  await expect(state(page, "Rejected")).toHaveAttribute("data-selected", "true");
  await page.keyboard.press("Delete");
  await expect(page.getByRole("dialog").getByTestId("delete-incoming").locator("li")).toHaveCount(2);
  await page.getByTestId("confirm-delete-state").click();
  await expect(state(page, "Ordered")).toHaveCount(0);
  await expect(state(page, "Rejected")).toHaveCount(0);
  await page.keyboard.press("ControlOrMeta+z");
  await expect(state(page, "Rejected")).toBeVisible();
  await expect(state(page, "Ordered")).toBeVisible();
});

test("F12 on an edge opens its gate, else its event, on the matching tab; the context menu offers the same actions", async ({ page }) => {
  const editor = await openProcess(page, "Purchase approval");
  await label(page, "approve").click();
  await page.keyboard.press("F12");
  await expect(tab(editor, "Gates")).toHaveAttribute("aria-selected", "true");
  await expect(editor.getByTestId("gate-form")).toHaveCount(1);

  await tab(editor, "Chart").click();
  await label(page, "reject").click();
  await page.keyboard.press("F12");
  await expect(tab(editor, "Events")).toHaveAttribute("aria-selected", "true");

  await tab(editor, "Chart").click();
  await state(page, "Review").click({ button: "right", position: { x: 60, y: 10 } });
  const menu = page.getByTestId("chart-menu");
  await expect(menu).toBeVisible();
  for (const item of ["Add state", "Add child state", "Draw a transition", "Rename", "Lay out its states", "Collapse", "Delete"])
    await expect(menu.getByRole("menuitem", { name: new RegExp(`^${item}`) })).toBeVisible();
  // Collapse: Review draws as one box with the count of what it hides, and the diagram remembers it.
  await menu.getByRole("menuitem", { name: /^Collapse/ }).click();
  await expect(state(page, "Review").getByTestId("state-hidden-count")).toHaveText("7");
  await expect(state(page, "Checking")).toHaveCount(0);
  await expect.poll(async () => (await diagramOf(page, PURCHASE_APPROVAL))?.members.find((m) => m.element === S(102))?.collapsed).toBe(true);
  await state(page, "Review").getByTestId("state-toggle").click();
  await expect(state(page, "Checking")).toBeVisible();
});

test("Shift+F12 on the chart lists where the selected state is used; a state selected inside a collapsed container is revealed", async ({ page }) => {
  const editor = await openProcess(page, "Purchase approval");
  // Shift+F12: the References panel for the state selected, not for the process.
  await state(page, "Approval").getByTitle("Approval", { exact: true }).click();
  await page.keyboard.press("Shift+F12");
  await expect(page.getByTestId("references-panel")).toBeVisible();
  await expect(page.getByTestId("references-title")).toContainText(S(110));

  // Review collapsed on the chart; Checking selected on the States grid; back on the chart, Review opens to show it.
  await state(page, "Review").click({ button: "right", position: { x: 60, y: 10 } });
  await page
    .getByTestId("chart-menu")
    .getByRole("menuitem", { name: /^Collapse/ })
    .click();
  await expect(state(page, "Checking")).toHaveCount(0);
  await expect.poll(async () => (await diagramOf(page, PURCHASE_APPROVAL))?.members.find((m) => m.element === S(102))?.collapsed).toBe(true);
  await tab(editor, "States").click();
  await editor.locator(`[data-testid="states-grid-row"][data-id="${S(104)}"] [data-column="name"]`).click();
  await tab(editor, "Chart").click();
  await expect(state(page, "Checking")).toBeVisible();
  await expect(state(page, "Checking")).toHaveAttribute("data-selected", "true");
  await expect.poll(async () => (await diagramOf(page, PURCHASE_APPROVAL))?.members.find((m) => m.element === S(102))?.collapsed).toBeUndefined();
  // The arrows move from it, as from any drawn state.
  await chart(page).focus();
  await page.keyboard.press("ArrowRight");
  await expect.poll(() => chart(page).getAttribute("data-selected")).toBe(S(105));
});
