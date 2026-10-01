// The custom type editor's Native types section: a row per dialect the project's databases use, a select that adds
// another dialect, each value saved through the draft as one undo step, and the mock's database view following it.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const EMAIL_ADDRESS = "01J92P0V04TDYE2C73WMNXVDBV";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

/** The saved native types of EmailAddress, read from the mock API. */
const savedNativeTypes = (page: Page) =>
  page.evaluate(async (id) => {
    const doc = (await (await fetch(`/api/model/elements/${id}`)).json()) as { json: { nativeTypes?: Record<string, string> } };
    return doc.json.nativeTypes ?? null;
  }, EMAIL_ADDRESS);

/** The native type of the customers.email column in the main database's view. */
const emailColumnNativeType = (page: Page) =>
  page.evaluate(async () => {
    const index = (await (await fetch("/api/model/index")).json()) as { id: string; kind: string; name: string }[];
    const main = index.find((r) => r.kind === "database" && r.name === "main")!;
    const body = (await (await fetch(`/api/databases/${main.id}/view`)).json()) as {
      view: { tables: { name: string; columns: { name: string; nativeType: string }[] }[] };
    };
    return body.view.tables.find((t) => t.name === "customers")?.columns.find((c) => c.name === "email")?.nativeType ?? null;
  });

test("a custom type's native types: one row per database dialect, another dialect added, each value one undo step", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("EmailAddress");
  await side.getByTestId("explorer-row-EmailAddress").dblclick();
  const editor = page.getByRole("region", { name: "Editor: EmailAddress" });
  await expect(editor).toBeVisible();

  const section = editor.getByRole("region", { name: "Native types" });
  // The billing fixture has one PostgreSQL database: its row is there, empty, and the other dialects can be added.
  const postgresql = section.getByLabel("postgresql", { exact: true });
  await expect(postgresql).toHaveValue("");
  await expect(postgresql).toHaveAttribute("placeholder", "From the type map");
  await expect(section.getByLabel("oracle", { exact: true })).toHaveCount(0);
  await expect(section.getByText(/\{length\}/).first()).toBeVisible();

  await postgresql.fill("citext");
  await postgresql.press("Tab");
  await expect.poll(() => savedNativeTypes(page)).toEqual({ postgresql: "citext" });
  await expect.poll(() => emailColumnNativeType(page)).toBe("citext");

  // Another dialect: a row appears; its value saves next to the first, in key order.
  await section.getByLabel("Add a dialect").selectOption("oracle");
  const oracle = section.getByLabel("oracle", { exact: true });
  await oracle.fill("varchar2({length} char)");
  await oracle.press("Tab");
  await expect.poll(() => savedNativeTypes(page)).toEqual({ oracle: "varchar2({length} char)", postgresql: "citext" });

  // Undo takes back the oracle value alone, then the postgresql value.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(() => savedNativeTypes(page)).toEqual({ postgresql: "citext" });
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(() => savedNativeTypes(page)).toBeNull();
  await expect(postgresql).toHaveValue("");
  await expect.poll(() => emailColumnNativeType(page)).toBe("varchar(254)");

  // Clearing a value with its button is one step too.
  await postgresql.fill("text");
  await postgresql.press("Tab");
  await expect.poll(() => savedNativeTypes(page)).toEqual({ postgresql: "text" });
  await section.getByRole("button", { name: "Clear the postgresql native type" }).click();
  await expect.poll(() => savedNativeTypes(page)).toBeNull();
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(() => savedNativeTypes(page)).toEqual({ postgresql: "text" });
});
