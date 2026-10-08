// Tags across the model (2026-10-07, the owner: a vocabulary created on a model that already uses tags turned every use
// into a note; "can I delete the tag right there and automatically remove it from all the uses?"): the Tags screen counts
// each tag's uses, lists the tags in use that no vocabulary declares, adds them, renames a tag everywhere and removes the
// checked tags everywhere, each one undo step; a domain's first tag declares the tags already in use with it. Mock-only: it
// writes elements into the mock model.
import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const LEDGER = "01K7TAG0000000000000000001";

async function createLedger(page: Page, tags: string[]) {
  const outcome = await page.evaluate(
    async ({ pkg, id, tags }) => {
      const res = await fetch("/api/model/elements", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          kind: "entity",
          id,
          name: "Ledger",
          package: pkg,
          tags,
          key: { attributes: ["01K7TAG0000000000000000002"], strategy: "uuid-v7" },
          attributes: [{ id: "01K7TAG0000000000000000002", name: "id", type: "uuid", required: true, tags: ["audit"] }],
        }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    { pkg: BILLING, id: LEDGER, tags },
  );
  expect(outcome).toBe("saved");
}

const tagsOf = (page: Page, id: string) =>
  page.evaluate(async (id) => ((await (await fetch(`/api/model/elements/${id}`)).json()) as { json: { tags?: string[] } }).json.tags ?? [], id);

test("the Tags screen counts uses, adds the tags in use, renames one everywhere and removes the checked ones everywhere", async ({ page }) => {
  await page.goto("/settings/tags");
  const tags = page.getByTestId("tag-vocabulary-editor");
  // Not strict, so a tag nothing declares can be used (and is a note).
  await tags.getByLabel("Strict: an undeclared tag is an error (MQ2006)").uncheck();
  await createLedger(page, ["audit", "billing"]);

  // audit: two uses (the entity and its attribute), declared nowhere.
  const undeclared = tags.getByTestId("tags-undeclared");
  await expect(undeclared.getByTestId("tag-undeclared-audit")).toContainText("2 uses · 1 element");
  await expect(tags.getByTestId("tag-used-billing")).toContainText("uses");
  // A key in use is renamed everywhere, not edited in place.
  await expect(tags.getByLabel("Key of tag 1")).toHaveAttribute("readonly", "");

  // Add it to the vocabulary: it is a row now, with its uses.
  await undeclared.getByTestId("tag-undeclared-audit").getByRole("button", { name: "Add" }).click();
  await expect(tags.getByTestId("tag-used-audit")).toContainText("2 uses");
  await expect(tags.getByTestId("tags-undeclared")).toHaveCount(0);

  // Rename billing everywhere: the vocabulary's entry and the uses.
  await tags.getByRole("button", { name: "Rename billing everywhere" }).click();
  const rename = page.getByRole("dialog", { name: "Rename billing everywhere" });
  await rename.getByLabel("New key").fill("finance");
  await rename.getByTestId("rename-tag-apply").click();
  await expect(page.getByTestId("notice")).toContainText("Renamed billing to finance everywhere.");
  await expect(tags.getByTestId("tag-used-finance")).toBeVisible();
  expect(await tagsOf(page, LEDGER)).toEqual(["audit", "finance"]);

  // Check two tags and remove them everywhere: the dialog lists both with their counts.
  await tags.getByLabel("Select tag audit").check();
  await tags.getByLabel("Select tag pii").check();
  await expect(tags.getByTestId("tags-selection")).toContainText("2 selected");
  await tags.getByTestId("tags-remove-selected").click();
  const remove = page.getByRole("dialog", { name: "Remove 2 tags everywhere?" });
  await expect(remove.getByTestId("remove-tags-list")).toContainText("audit");
  await expect(remove.getByTestId("remove-tags-list")).toContainText("pii");
  await remove.getByTestId("remove-tags-apply").click();
  await expect(page.getByTestId("notice")).toContainText("Removed 2 tags everywhere.");
  await expect(tags.getByTestId("tag-used-audit")).toHaveCount(0);
  await expect(tags.getByTestId("tag-used-pii")).toHaveCount(0);
  expect(await tagsOf(page, LEDGER)).toEqual(["finance"]);

  // One undo step puts both back, in the vocabulary and on the entity.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(tags.getByTestId("tag-used-pii")).toBeVisible();
  await expect.poll(() => tagsOf(page, LEDGER)).toEqual(["audit", "finance"]);
});

test("a domain's first tag declares the tags already in use in the domain with it", async ({ page }) => {
  await page.goto("/settings/tags");
  await page.getByTestId("tag-vocabulary-editor").getByLabel("Strict: an undeclared tag is an error (MQ2006)").uncheck();
  await createLedger(page, ["ledger-only"]);

  const side = page.getByRole("complementary", { name: "Explorer" });
  await page.getByRole("navigation", { name: "Explorers" }).getByRole("button", { name: "Domain model", exact: true }).click();
  await side.getByTestId("explorer-domain-Billing").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Open" }).click();
  const editor = page.getByTestId("element-editor");
  await editor.getByRole("tab", { name: "Tags" }).click();
  // Used in the domain, declared nowhere on its chain: listed (audit too, from the attribute).
  await expect(editor.getByTestId("tag-undeclared-ledger-only")).toContainText("1 use");
  await editor.getByTestId("tags-add").click();
  await expect(page.getByTestId("notice")).toContainText("Also declared the 2 tags already in use.");
  await expect(editor.getByTestId("tag-used-ledger-only")).toContainText("1 use");
  await expect(editor.getByTestId("tag-used-audit")).toContainText("1 use");
  await expect(editor.getByLabel("Key of tag 1")).toHaveValue("tag1");
  await expect(editor.getByTestId("tags-undeclared")).toHaveCount(0);
});
