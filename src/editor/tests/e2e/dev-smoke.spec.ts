// `dev` project: the Vite dev server (the only server that enforces server.fs.allow) serves the
// editor in mock mode, and the Invoice card is on "Billing overview".
import { card, expect, openEditor, test } from "./fixtures";

test("dev server shows Invoice on Billing overview", async ({ page }) => {
  await openEditor(page);
  await expect(card(page, "Invoice")).toBeVisible();
});
