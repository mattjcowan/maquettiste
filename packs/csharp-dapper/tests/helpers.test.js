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

// A typed expression tree as templates pass it (check.condition, guard.condition): node, op or call, args, value, type and nullable,
// and text, which for a path is its name as the expression writes it. Specs are "path|C# expression|C# type|kind|member=C# member,...".
const path = (text, kind, nullable) => ({ node: "path", text, type: { kind, bits: kind === "int" ? 32 : 0 }, nullable });
const literal = (value, kind, bits = 0) => ({ node: "value", value, type: { kind, bits }, nullable: false });
const ref = (value) => ({ node: "ref", value, type: { kind: "enum", bits: 0 }, nullable: false });
const op = (name, args, nullable, kind = "bool") => ({ node: "op", op: name, args, type: { kind, bits: 0 }, nullable });
const call = (name, args, nullable, kind = "bool") => ({ node: "call", call: name, args, type: { kind, bits: kind === "int" ? 32 : 0 }, nullable });
const invoice = "issuedOn|IssuedOn|DateOnly?|value|;dueOn|DueOn|DateOnly?|value|;total|Total|decimal|value|;number|Number|string|value|;"
  + "note|Note|string?|value|;status|Status|InvoiceStatus|enum|Draft=Draft,Paid=Paid";

maquettiste.test("expr_cs writes three-valued logic with lifted nulls", (assert, helpers) => {
  const dueAfterIssue = op("le", [path("issuedOn", "date", true), path("dueOn", "date", true)], true);
  assert.equal(helpers.expr_cs(dueAfterIssue, invoice), "(IssuedOn is null || DueOn is null ? (bool?)null : (IssuedOn.Value <= DueOn.Value))");
  assert.equal(helpers.expr_cs(op("and", [dueAfterIssue, op("ge", [path("total", "decimal", false), literal("0.50", "decimal")], false)], true), invoice),
    "((IssuedOn is null || DueOn is null ? (bool?)null : (IssuedOn.Value <= DueOn.Value)) & (bool?)(Total >= 0.50m))");
  assert.equal(helpers.expr_cs(op("or", [op("isNull", [path("dueOn", "date", true)], false), op("gt", [path("total", "decimal", false), literal(0, "int", 32)], false)], false), invoice),
    "((DueOn is null) || (Total > 0))");
  assert.equal(helpers.expr_cs(call("length", [path("note", "string", true)], true, "int"), invoice),
    "(Note is null ? (int?)null : System.Linq.Enumerable.Count(Note!.EnumerateRunes()))");
});

maquettiste.test("expr_cs reads a guard's context and payload, a missing payload member being null", (assert, helpers) => {
  const guard = "total|call.Context.Total|decimal|value|;event.amount|call.Event.Get<decimal?>(\"amount\")|decimal?|value|";
  assert.equal(helpers.expr_cs(op("ge", [path("event.amount", "decimal", true), path("total", "decimal", true)], true), guard),
    "(call.Event.Get<decimal?>(\"amount\") is null ? (bool?)null : (call.Event.Get<decimal?>(\"amount\").Value >= call.Context.Total))");
});

maquettiste.test("expr_cs compares enum members by their C# names and strings ordinally", (assert, helpers) => {
  assert.equal(helpers.expr_cs(op("eq", [path("status", "enum", false), ref("Paid")], false), invoice), "(Status == InvoiceStatus.Paid)");
  assert.equal(helpers.expr_cs(op("in", [path("status", "enum", false), ref("Draft"), ref("Paid")], false), invoice),
    "((Status == InvoiceStatus.Draft) || (Status == InvoiceStatus.Paid))");
  assert.equal(helpers.expr_cs(op("lt", [path("number", "string", false), literal("INV-5", "string")], false), invoice),
    "(string.CompareOrdinal(Number, \"INV-5\") < 0)");
});

maquettiste.test("expr_cs matches in .NET's linear-time mode with $ anchoring the end of the string", (assert, helpers) => {
  const options = "System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.Singleline";
  assert.equal(helpers.expr_cs(call("matches", [path("number", "string", false), literal("^INV-[0-9]+$", "string")], false), invoice),
    "System.Text.RegularExpressions.Regex.IsMatch(Number, \"^INV-[0-9]+\\\\z\", " + options + ")");
  assert.equal(helpers.expr_cs(call("matches", [path("number", "string", false), literal("^[$]\\$", "string")], false), invoice),
    "System.Text.RegularExpressions.Regex.IsMatch(Number, \"^[$]\\\\$\", " + options + ")");
});

maquettiste.test("expr_cs returns empty text for what it does not render", (assert, helpers) => {
  assert.equal(helpers.expr_cs(op("gt", [op("div", [path("total", "decimal", false), literal(2, "int", 32)], false, "decimal"), literal(1, "int", 32)], false), invoice), "");
  assert.equal(helpers.expr_cs(op("gt", [path("amount.value", "decimal", false), literal(1, "int", 32)], false), invoice), "");
  assert.equal(helpers.expr_cs(null, invoice), "");
});

maquettiste.test("cs_line keeps a comment on one line", (assert, helpers) => {
  assert.equal(helpers.cs_line("Sends the invoice\r\nand records it\nonce."), "Sends the invoice and records it once.");
  assert.equal(helpers.cs_line(null), "");
});
