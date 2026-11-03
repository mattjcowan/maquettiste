// Tests of the helpers in helpers.js. `maquettiste pack test csharp-dapper` runs them after the billing-small case; each test gets
// the assert API and the pack's helpers. Templates pass resolved elements; a test passes the plain values a helper reads.

maquettiste.test("join_path skips empty segments", (assert, helpers) => {
  assert.equal(helpers.join_path("Generated", "", "Billing", "Invoice.g.cs"), "Generated/Billing/Invoice.g.cs");
  assert.equal(helpers.join_path("", null, "Invoice.g.cs"), "Invoice.g.cs");
});

maquettiste.test("package_folder follows the package's qualified name", (assert, helpers) => {
  assert.equal(helpers.package_folder({ package: { qualifiedName: "Billing.Receivables" } }), "Billing/Receivables");
  assert.equal(helpers.package_folder({ package: null }), "");
});

maquettiste.test("cs_ident prefixes C# keywords", (assert, helpers) => {
  assert.equal(helpers.cs_ident("class"), "@class");
  assert.equal(helpers.cs_ident("total"), "total");
});

maquettiste.test("cs_class prefixes a name that starts with a digit or is empty", (assert, helpers) => {
  assert.equal(helpers.cs_class("2026Invoices"), "Q2026Invoices");
  assert.equal(helpers.cs_class(""), "Q");
  assert.equal(helpers.cs_class("OpenInvoices"), "OpenInvoices");
});

maquettiste.test("cs_value_type knows the value types of the type map", (assert, helpers) => {
  assert.ok(helpers.cs_value_type("decimal"));
  assert.ok(helpers.cs_value_type("DateOnly"));
  assert.ok(!helpers.cs_value_type("string"));
});

maquettiste.test("ulid_uuid turns a ULID into the same 128 bits as a UUID", (assert, helpers) => {
  assert.equal(helpers.ulid_uuid("00000000000000000000000001"), "00000000-0000-0000-0000-000000000001");
  assert.equal(helpers.ulid_uuid("7ZZZZZZZZZZZZZZZZZZZZZZZZZ"), "ffffffff-ffff-ffff-ffff-ffffffffffff");
  assert.equal(helpers.ulid_uuid("not-a-ulid"), "");
});

// Specs are "name|C# name|C# type|kind|member=C# member,..." joined with ";".
const context = "total|Total|decimal|decimal;reminders|Reminders|int|int;status|Status|InvoiceStatus|enum|sent=Sent,paid=Paid";
const payload = "amount|Amount|decimal|decimal";

maquettiste.test("cs_expression translates a guard to C#", (assert, helpers) => {
  assert.equal(helpers.cs_expression("context.total > 1000", "guard", context, payload), "call.Context.Total > 1000");
  assert.equal(helpers.cs_expression("context.status === 'paid'", "guard", context, payload), "call.Context.Status == InvoiceStatus.Paid");
  assert.equal(helpers.cs_expression("event.payload.amount >= context.total", "guard", context, payload),
    "call.Event.Get<decimal>(\"amount\") >= call.Context.Total");
});

maquettiste.test("cs_expression translates an action to a context update", (assert, helpers) => {
  assert.equal(helpers.cs_expression("({ reminders: context.reminders + 1 })", "action", context, payload),
    "call.Context with { Reminders = call.Context.Reminders + 1 }");
  assert.equal(helpers.cs_expression("({ status: 'sent' })", "action", context, payload), "call.Context with { Status = InvoiceStatus.Sent }");
});

maquettiste.test("cs_expression returns empty text outside the subset", (assert, helpers) => {
  assert.equal(helpers.cs_expression("Math.max(context.total, 0)", "guard", context, payload), "");
  assert.equal(helpers.cs_expression("({ reminders: 1.5 })", "action", context, payload), "");
  assert.equal(helpers.cs_expression("", "guard", context, payload), "");
});

maquettiste.test("cs_line keeps a comment on one line", (assert, helpers) => {
  assert.equal(helpers.cs_line("Sends the invoice\r\nand records it\nonce."), "Sends the invoice and records it once.");
  assert.equal(helpers.cs_line(null), "");
});
