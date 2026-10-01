// Rename pack (the owner: "how do i rename the pack?"): the pack editor's header offers Rename pack… beside Remove
// pack…; the dialog takes a name under the pack-name rule and shows the folder it moves to. Renaming moves the folder,
// the packs.<pack> settings entry and the manifest together: the tab and the explorer row follow the new name, and the
// next plan finds the generated files still tracked, so it deletes nothing.
import AxeBuilder from "@axe-core/playwright";
import { expect, test, workspace } from "./fixtures";

test("rename a pack from its editor; its tab, its row and its generated files follow", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");

  // Generate first, so the pack's manifest records files.
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("sql-ddl:");
  await page.getByTestId("apply").click();
  await expect(page.getByTestId("apply-result")).toContainText("succeeded");
  const outputs = await page.evaluate(() => fetch("/api/packs/sql-ddl/outputs").then((r) => r.json()));
  const written = (outputs.outputs as { path: string }[]).length;
  expect(written).toBeGreaterThan(0);

  const tree = page.getByRole("tree", { name: "Packs" });
  await tree.getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  const rename = page.getByTestId("pack-rename");
  await expect(rename).toHaveAttribute("title", /generated files stay tracked/);
  await expect(page.getByTestId("pack-remove")).toBeVisible();

  // The name is checked as it is typed; Cancel leaves everything as it was.
  await rename.click();
  const dialog = page.getByTestId("rename-pack-dialog");
  const name = dialog.getByLabel("Name");
  await expect(name).toHaveValue("sql-ddl");
  await expect(dialog).toContainText("Lowercase letters and digits separated by single hyphens");
  await expect(dialog).toContainText("This cannot be undone from the editor");
  await expect(page.getByTestId("rename-pack-tracked")).toHaveText(
    `The ${written} files it generated stay where they are and stay tracked under the new name.`,
  );
  await name.fill("csharp-dapper");
  await expect(dialog).toContainText("A pack named csharp-dapper exists.");
  await expect(page.getByTestId("rename-pack-confirm")).toBeDisabled();
  await name.fill("ddl--x");
  await expect(page.getByTestId("rename-pack-confirm")).toBeDisabled();
  await name.fill("ddl");
  await expect(page.getByTestId("rename-pack-moves")).toHaveText(
    "Moves .maquettiste/templates/sql-ddl/ to .maquettiste/templates/ddl/ and packs.sql-ddl to packs.ddl in maquettiste.json.",
  );
  const axe = await new AxeBuilder({ page }).include("[data-testid=rename-pack-dialog]").analyze();
  expect(axe.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toBeHidden();
  const before = await page.evaluate(() => fetch("/api/packs").then((r) => r.json()));
  expect((before.packs as { name: string }[]).map((p) => p.name)).toEqual(["csharp-dapper", "sql-ddl"]);

  // Rename: the tab and the row take the new name, the settings entry moves.
  await rename.click();
  await dialog.getByLabel("Name").fill("ddl");
  await page.getByTestId("rename-pack-confirm").click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId("pack-tab-sql-ddl")).toHaveCount(0);
  await expect(page.getByTestId("pack-tab-ddl")).toBeVisible();
  await expect(page.getByTestId("pack-editor-title")).toContainText("ddl");
  await expect(tree.getByTestId("pack-row-p:sql-ddl")).toHaveCount(0);
  await expect(tree.getByTestId("pack-row-p:ddl")).toBeVisible();
  await expect(page.getByTestId("notice")).toContainText("Renamed pack sql-ddl to ddl.");
  const settings = await page.evaluate(() => fetch("/api/project/settings").then((r) => r.json()));
  expect(settings.json.packs?.["sql-ddl"]).toBeUndefined();
  expect(settings.json.packs?.ddl).toBeDefined();
  const moved = await page.evaluate(() => fetch("/api/packs/ddl/outputs").then((r) => r.json()));
  expect((moved.outputs as unknown[]).length).toBe(written);

  // The next plan finds the files tracked under the new name: nothing to delete.
  await page.getByTestId("generate-tabs").getByRole("tab").first().click();
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("ddl:");
  await expect(page.getByTestId("plan-summary")).not.toContainText("sql-ddl");
  await expect(page.getByTestId("plan-summary")).not.toContainText("to delete");
});

test("the generation hints that name the pack move with it, as one undo step", async ({ page }) => {
  const customer = "01J92P0V0ETQKXXP951CMMNHH3";
  await page.goto("/generate");
  await workspace(page, "Generate");
  // A hint keyed by the pack: on the entity and on one of its attributes.
  await page.evaluate(async (id) => {
    const doc = await fetch(`/api/model/elements/${id}`).then((r) => r.json());
    const json = doc.json;
    json.generation = { "sql-ddl": { skip: true } };
    json.attributes[0].generation = { "sql-ddl": { rename: "customer_key" } };
    await fetch(`/api/model/elements/${id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${doc.hash}"` },
      body: JSON.stringify(json),
    });
  }, customer);
  const tree = page.getByRole("tree", { name: "Packs" });
  await tree.getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await page.getByTestId("pack-rename").click();
  const dialog = page.getByTestId("rename-pack-dialog");
  await expect(page.getByTestId("rename-pack-hints")).toHaveCount(0);
  await dialog.getByLabel("Name").fill("ddl");
  const update = dialog.getByRole("checkbox", { name: "Also update the generation hints that name this pack (1 element)" });
  await expect(update).toBeChecked();
  await page.getByTestId("rename-pack-confirm").click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId("notice")).toContainText("Renamed pack sql-ddl to ddl and moved the generation hints of 1 element.");
  const read = () => page.evaluate((id) => fetch(`/api/model/elements/${id}`).then((r) => r.json()), customer);
  let doc = await read();
  expect(doc.json.generation).toEqual({ ddl: { skip: true } });
  expect(doc.json.attributes[0].generation).toEqual({ ddl: { rename: "customer_key" } });

  // The hint update is one undo step; the pack keeps its new name.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(async () => (await read()).json.generation).toEqual({ "sql-ddl": { skip: true } });
  doc = await read();
  expect(doc.json.attributes[0].generation).toEqual({ "sql-ddl": { rename: "customer_key" } });
  await expect(tree.getByTestId("pack-row-p:ddl")).toBeVisible();
});

test("unticked, the hints keep the old name", async ({ page }) => {
  const customer = "01J92P0V0ETQKXXP951CMMNHH3";
  await page.goto("/generate");
  await workspace(page, "Generate");
  await page.evaluate(async (id) => {
    const doc = await fetch(`/api/model/elements/${id}`).then((r) => r.json());
    await fetch(`/api/model/elements/${id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${doc.hash}"` },
      body: JSON.stringify({ ...doc.json, generation: { "sql-ddl": { skip: true } } }),
    });
  }, customer);
  const tree = page.getByRole("tree", { name: "Packs" });
  await tree.getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await page.getByTestId("pack-rename").click();
  const dialog = page.getByTestId("rename-pack-dialog");
  await dialog.getByLabel("Name").fill("ddl");
  await dialog.getByRole("checkbox", { name: /Also update the generation hints/ }).click();
  await page.getByTestId("rename-pack-confirm").click();
  await expect(page.getByTestId("notice")).toContainText("Renamed pack sql-ddl to ddl. The generation hints of 1 element still name sql-ddl.");
  const doc = await page.evaluate((id) => fetch(`/api/model/elements/${id}`).then((r) => r.json()), customer);
  expect(doc.json.generation).toEqual({ "sql-ddl": { skip: true } });
});

test("a stale pack.json hash renames nothing and says why", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  const tree = page.getByRole("tree", { name: "Packs" });
  await tree.getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  await page.getByTestId("pack-rename").click();
  await page.getByTestId("rename-pack-dialog").getByLabel("Name").fill("ddl");

  // pack.json changes behind the editor's back (another window, the CLI).
  await page.evaluate(async () => {
    const pack = await fetch("/api/packs/sql-ddl").then((r) => r.json());
    await fetch("/api/packs/sql-ddl", {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${pack.hash}"` },
      body: JSON.stringify({ ...pack.document, description: "Changed elsewhere." }),
    });
  });
  await page.getByTestId("rename-pack-confirm").click();
  await expect(page.getByTestId("rename-pack-dialog").getByRole("alert")).toContainText("pack.json changed since this editor read it; nothing was renamed.");
  const packs = await page.evaluate(() => fetch("/api/packs").then((r) => r.json()));
  expect((packs.packs as { name: string }[]).map((p) => p.name)).toEqual(["csharp-dapper", "sql-ddl"]);
  await expect(page.getByTestId("pack-tab-sql-ddl")).toBeVisible();
});
