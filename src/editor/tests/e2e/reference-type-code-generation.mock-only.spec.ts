// A reference type's Code generation and JSON tabs (Reference data screen): custom properties from an extension that
// applies to reference types, an object-valued one drawn as a nested group, an array of objects as a JSON box that
// saves only valid JSON, and the type's document in the JSON tab.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const TYPE = "01K0RG00000000000000000001";

const SCHEMA = {
  name: "ref-hints",
  description: "Hints for reference types.",
  appliesTo: { kinds: ["reference-type"] },
  properties: {
    cache: { type: "object", properties: { ttl: { type: "integer" }, scope: { type: "string" } } },
    aliases: { type: "array", items: { type: "object" } },
  },
};

async function seed(page: Page): Promise<void> {
  const out = await page.evaluate(
    async ({ type, schema }) => {
      const put = await fetch("/api/extensions/file?path=ref-hints.json", {
        method: "PUT",
        headers: { "Content-Type": "application/json", "If-None-Match": "*" },
        body: JSON.stringify({ text: JSON.stringify(schema) }),
      });
      const res = await fetch("/api/model/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          operations: [
            {
              op: "create",
              element: {
                kind: "reference-type",
                id: type,
                name: "Currency",
                code: { id: "01K0RG00000000000000000002" },
                label: { id: "01K0RG00000000000000000003" },
              },
            },
          ],
        }),
      });
      return [put.status, ((await res.json()) as { outcome: string }).outcome];
    },
    { type: TYPE, schema: SCHEMA },
  );
  expect(out).toEqual([201, "saved"]);
}

const properties = (page: Page) =>
  page.evaluate(async (id) => {
    const r = await fetch(`/api/model/elements/${id}`);
    return ((await r.json()) as { json: { properties?: unknown } }).json.properties;
  }, TYPE);

test("a reference type has Code generation and JSON tabs", async ({ page }) => {
  await openEditor(page);
  await seed(page);
  await workspace(page, "Reference data");
  await page.getByTestId("explorer-reference-data").getByTestId("explorer-row-Currency").click();
  await expect(page.getByTestId("reference-type-title")).toHaveText("Currency");

  await page.getByRole("tab", { name: "Code generation" }).click();
  const form = page.getByTestId("custom-properties");
  await expect(form.getByTestId("custom-object-cache")).toBeVisible();
  await form.getByLabel("ttl").fill("30");
  await expect.poll(() => properties(page)).toEqual({ cache: { ttl: 30 } });
  await form.getByLabel("scope").fill("all");
  await expect.poll(() => properties(page)).toEqual({ cache: { ttl: 30, scope: "all" } });

  // An array of objects is a JSON box: invalid text shows an error and saves nothing.
  const aliases = form.getByLabel("aliases");
  await aliases.fill('[{"a": ');
  await expect(form.getByRole("alert")).toContainText("Not valid JSON");
  await aliases.fill('[{"a": 1}]');
  await expect(form.getByRole("alert")).toHaveCount(0);
  await expect.poll(() => properties(page)).toEqual({ cache: { ttl: 30, scope: "all" }, aliases: [{ a: 1 }] });
  await expect(page.getByTestId("reference-data")).not.toContainText("[object Object]");

  await page.getByRole("tab", { name: "JSON" }).click();
  await expect(page.getByTestId("reference-data")).toContainText('"ttl": 30');
});
