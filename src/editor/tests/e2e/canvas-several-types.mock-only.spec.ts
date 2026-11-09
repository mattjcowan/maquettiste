// Relations with several types at an end on the canvas (several-types-design.md section 11): a junction node for three types, bundled lines
// for two, a box for any entity, the toggle that collapses the lines to a list and back (saved as the relation member's `collapsed`), the
// node's position saved by dragging, "+N not shown", and a diagram that has none of these left as it was.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const OVERVIEW = "01J92P0V2164SDBW687ZV6E1MV";
const ulid = (n: number) => `01J92P0VZY${String(n).padStart(16, "0")}`;
const ID = {
  comment: ulid(1),
  part: ulid(2),
  assembly: ulid(3),
  document: ulid(4),
  tool: ulid(5),
  diagram: ulid(6),
  junction: ulid(7),
  bundle: ulid(8),
  any: ulid(9),
  hidden: ulid(10),
  key: (n: number) => ulid(20 + n),
  end: (n: number) => ulid(40 + n),
};

type Member = { element: string; x?: number; y?: number; collapsed?: boolean };

const entity = (id: string, name: string, n: number) => ({
  kind: "entity",
  id,
  name,
  package: BILLING,
  key: { attributes: [ID.key(n)], strategy: "uuid-v7" },
  attributes: [{ id: ID.key(n), name: "id", type: "uuid", required: true }],
});
const relation = (id: string, name: string, n: number, far: Record<string, unknown>) => ({
  kind: "relation",
  id,
  name,
  package: BILLING,
  ends: [
    { id: ID.end(n), entity: ID.comment, role: "comment", navigation: `${name}Comments` },
    { id: ID.end(n + 1), role: "attachedTo", navigation: `${name}Target`, max: 1, ...far },
  ],
});

/** Creates the entities, the relations and a diagram that shows them, on the mock server. */
async function seed(page: Page, bundleAt?: { x: number; y: number }) {
  const operations = [
    entity(ID.comment, "Comment", 1),
    entity(ID.part, "Part", 2),
    entity(ID.assembly, "Assembly", 3),
    entity(ID.document, "Document", 4),
    entity(ID.tool, "Tool", 5),
    relation(ID.junction, "AttachedTo", 10, { types: [{ entity: ID.part }, { entity: ID.assembly }, { entity: ID.document }] }),
    relation(ID.bundle, "Reviews", 12, { types: [{ entity: ID.part }, { entity: ID.assembly }] }),
    relation(ID.any, "Tagged", 14, { anyEntity: true }),
    // Tool is not on the diagram: its entry is the "+1 not shown".
    relation(ID.hidden, "Uses", 16, { types: [{ entity: ID.part }, { entity: ID.tool }] }),
  ].map((element) => ({ op: "create", element }));
  const members: Member[] = [
    { element: ID.comment, x: 0, y: 300 },
    { element: ID.part, x: 700, y: 0 },
    { element: ID.assembly, x: 700, y: 260 },
    { element: ID.document, x: 700, y: 520 },
    ...[ID.bundle, ID.junction, ID.any, ID.hidden].map((element) => ({ element, ...(element === ID.bundle && bundleAt ? bundleAt : {}) })),
  ];
  operations.push({ op: "create", element: { kind: "diagram", id: ID.diagram, name: "Attachments", package: BILLING, members } } as never);
  const outcome = await page.evaluate(
    async (body) => {
      const res = await fetch("/api/model/batch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      return (await res.json()) as { outcome: string; diagnostics?: { rule: string; message: string }[] };
    },
    { operations },
  );
  expect(outcome.outcome, JSON.stringify(outcome.diagnostics)).toBe("saved");
}

const diagram = (page: Page, id: string) =>
  page.evaluate(async (diagramId) => ((await (await fetch(`/api/model/elements/${diagramId}`)).json()) as { json: { members: Member[] } }).json, id);
const member = async (page: Page, element: string) => (await diagram(page, ID.diagram)).members.find((m) => m.element === element);

async function openAttachments(page: Page, bundleAt?: { x: number; y: number }) {
  await openEditor(page);
  await seed(page, bundleAt);
  await page.getByTestId("diagram-picker").selectOption({ label: "Attachments" });
  await expect(page.getByTestId("relation-node-AttachedTo")).toBeVisible();
}

const lines = (page: Page, relationId: string) => page.locator(`.react-flow__edge[data-id^="${relationId}"]`);

test("the starting looks: a junction for three types, bundled lines for two, a box for any entity, and +N not shown", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  const overview = await (async () => {
    await openEditor(page);
    return JSON.stringify((await page.evaluate(async (id) => (await (await fetch(`/api/model/elements/${id}`)).json()) as unknown, OVERVIEW)) ?? null);
  })();
  await seed(page);
  await page.getByTestId("diagram-picker").selectOption({ label: "Attachments" });

  // Three types: one line to the node, one from it to each card.
  await expect(page.getByTestId("relation-node-AttachedTo")).toBeVisible();
  await expect(lines(page, ID.junction)).toHaveCount(4);
  await expect(page.getByTestId("relation-label-AttachedTo")).toBeVisible();
  await expect(page.getByTestId("relation-not-shown-AttachedTo")).toHaveCount(0);

  // Two types, both cards: bundled lines, a fork with no node of its own to drag.
  await expect(page.getByTestId("relation-fork-Reviews")).toBeAttached();
  await expect(lines(page, ID.bundle)).toHaveCount(3);
  await expect(page.getByTestId("relation-node-Reviews")).toHaveCount(0);

  // Any entity: one line to a box that says so.
  await expect(page.getByTestId("relation-any-Tagged")).toContainText("Any entity");
  await expect(lines(page, ID.any)).toHaveCount(1);
  // The box has no toggle: it has one entry.
  await expect(page.getByTestId("relation-toggle-Tagged")).toHaveCount(0);

  // Two types, one with no card on the canvas: a junction that says so, with the name in its tooltip.
  const hidden = page.getByTestId("relation-not-shown-Uses");
  await expect(hidden).toHaveText("+1 not shown");
  await expect(hidden).toHaveAttribute("title", "Tool");
  await expect(lines(page, ID.hidden)).toHaveCount(2);

  // Each line shows its end's role and cardinality (UML multiplicities by default).
  await expect(page.getByTestId("relation-end-AttachedTo-0")).toHaveText("comment *");
  await expect(page.getByTestId("relation-end-AttachedTo-1").first()).toHaveText("attachedTo 0..1");

  // A diagram that uses none of this is not touched by any of it.
  await page.getByTestId("diagram-picker").selectOption({ label: "Billing overview" });
  await expect(page.locator(".react-flow__node")).toHaveCount(5);
  expect(JSON.stringify(await page.evaluate(async (id) => (await (await fetch(`/api/model/elements/${id}`)).json()) as unknown, OVERVIEW))).toBe(overview);
});

test("the toggle collapses the lines to a list and back, saved on the relation's member; dragging the node saves its position", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openAttachments(page);
  const toggle = page.getByTestId("relation-toggle-AttachedTo");
  await expect(toggle).toHaveAttribute("title", "Collapse lines");
  expect(await member(page, ID.junction)).toEqual({ element: ID.junction });

  // Drag the junction: its position is saved in the member's x and y.
  const node = page.getByTestId("relation-node-AttachedTo");
  const box = (await node.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 40, box.y + box.height / 2 + 60, { steps: 8 });
  await page.mouse.up();
  await expect.poll(async () => (await member(page, ID.junction))?.x).toEqual(expect.any(Number));
  const placed = (await member(page, ID.junction))!;
  expect(placed.y).toEqual(expect.any(Number));
  expect(placed.collapsed).toBeUndefined();

  // Collapse: one line to a box that lists every entry; collapsed is saved, the position is kept.
  await toggle.click();
  await expect(page.getByTestId("relation-list-AttachedTo")).toBeVisible();
  await expect(page.getByTestId("relation-list-AttachedTo").getByTestId("relation-list-entry")).toHaveText(["Part", "Assembly", "Document"]);
  await expect(lines(page, ID.junction)).toHaveCount(1);
  await expect(page.getByTestId("relation-toggle-AttachedTo")).toHaveAttribute("title", "Expand lines");
  await expect.poll(async () => (await member(page, ID.junction))?.collapsed).toBe(true);
  expect(await member(page, ID.junction)).toMatchObject({ x: placed.x, y: placed.y });

  // The list's own toggle and the node's context menu do the same.
  await page.getByTestId("relation-node-toggle-AttachedTo").click();
  await expect(page.getByTestId("relation-node-AttachedTo")).toBeVisible();
  await expect(lines(page, ID.junction)).toHaveCount(4);
  await expect.poll(async () => (await member(page, ID.junction))?.collapsed).toBeUndefined();

  await page.getByTestId("relation-node-AttachedTo").click({ button: "right" });
  const item = page.getByTestId("relation-lines-menu-toggle");
  await expect(item).toHaveText("Collapse lines");
  await item.click();
  await expect(page.getByTestId("relation-list-AttachedTo")).toBeVisible();
  await expect.poll(async () => (await member(page, ID.junction))?.collapsed).toBe(true);

  // The toggle changes the drawing only: the relation is as it was created.
  const saved = await page.evaluate(
    async (id) => ((await (await fetch(`/api/model/elements/${id}`)).json()) as { json: { ends: { types?: unknown[] }[] } }).json,
    ID.junction,
  );
  expect(saved.ends[1].types).toHaveLength(3);
});

test("a click on a line, node or box selects the relation, and Auto-layout keeps the looks", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openAttachments(page);
  await page.getByTestId("relation-label-Reviews").click();
  await expect(page.getByRole("region", { name: /^Inspector: / })).toContainText("Reviews");
  await page.getByTestId("relation-any-Tagged").click();
  await expect(page.getByRole("region", { name: /^Inspector: / })).toContainText("Tagged");

  await page.getByTestId("auto-layout").click();
  await expect(page.getByTestId("auto-layout")).toHaveText(/Auto-layout/);
  await page.waitForTimeout(600);
  await expect(page.getByTestId("relation-node-AttachedTo")).toBeVisible();
  await expect(lines(page, ID.junction)).toHaveCount(4);
  await expect(lines(page, ID.bundle)).toHaveCount(3);
  // The fork of bundled lines is placed by the cards: no position is saved for it.
  expect(await member(page, ID.bundle)).toEqual({ element: ID.bundle });
});

test("the fork of bundled lines is placed by the cards: a stored position is ignored, the trunk runs straight, and nothing is saved for it", async ({
  page,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  // A position saved from before the look became a bundle (a junction that was dragged, then a type card was removed).
  await openAttachments(page, { x: 3000, y: 3000 });
  const centre = async (selector: string) => {
    const box = (await page.locator(selector).first().boundingBox())!;
    return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  };
  const fork = () => centre('[data-testid="relation-fork-Reviews"]');
  const comment = await centre(`.react-flow__node[data-id="${ID.comment}"]`);
  const before = await fork();
  expect(Math.abs(before.y - comment.y), JSON.stringify({ before, comment })).toBeLessThan(3);
  expect(before.x).toBeLessThan(comment.x + 700);

  // Drag Part away: the fork follows the cards instead of staying where the file said.
  const part = page.locator(`.react-flow__node[data-id="${ID.part}"]`);
  const box = (await part.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + 10);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 120, box.y - 100, { steps: 8 });
  await page.mouse.up();
  await expect.poll(async () => Math.abs((await fork()).x - before.x) + Math.abs((await fork()).y - before.y)).toBeGreaterThan(5);
  // Auto-layout and the drag never write the fork's position; the stored one is left as the file had it.
  await page.getByTestId("auto-layout").click();
  await page.waitForTimeout(600);
  expect(await member(page, ID.bundle)).toEqual({ element: ID.bundle, x: 3000, y: 3000 });
});

test("clicking a line of an already selected relation keeps it selected", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openAttachments(page);
  const inspector = page.getByRole("region", { name: /^Inspector: / });
  await page.getByTestId("relation-label-Reviews").click();
  await expect(inspector).toContainText("Reviews");
  for (const index of [0, 1, 2]) {
    await lines(page, ID.bundle).nth(index).locator(".react-flow__edge-interaction").dispatchEvent("click");
    await expect(inspector).toContainText("Reviews");
  }
  // A click on a branch of an unselected relation selects it.
  await lines(page, ID.junction).nth(1).locator(".react-flow__edge-interaction").dispatchEvent("click");
  await expect(inspector).toContainText("AttachedTo");
});
