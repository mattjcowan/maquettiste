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

// A typed expression tree as templates pass it (check.condition, guard.condition): node, op or call, args, value (and valueText, its exact
// text), type and nullable, and text, which for a path is its name as the expression writes it. Specs are "path|C# expression|C# type|
// kind|member=C# member,..."; compare() names the function that orders strings by code point.
const path = (text, kind, nullable, bits = kind === "int" ? 32 : 0) => ({ node: "path", text, type: { kind, bits }, nullable });
const literal = (value, kind, bits = 0, valueText = undefined) => ({ node: "value", value, valueText, type: { kind, bits }, nullable: false });
const ref = (value) => ({ node: "ref", value, type: { kind: "enum", bits: 0 }, nullable: false });
const op = (name, args, nullable, kind = "bool", bits = 0) => ({ node: "op", op: name, args, type: { kind, bits }, nullable });
const call = (name, args, nullable, kind = "bool") => ({ node: "call", call: name, args, type: { kind, bits: kind === "int" ? 32 : 0 }, nullable });
const invoice = "compare()|CodePoints.Compare|int|value|;issuedOn|IssuedOn|DateOnly?|value|;dueOn|DueOn|DateOnly?|value|;total|Total|decimal|value|;"
  + "number|Number|string|value|;note|Note|string?|value|;status|Status|InvoiceStatus|enum|Draft=Draft,Paid=Paid;qty|Qty|int?|value|;big|Big|long?|value|";

maquettiste.test("expr_cs writes three-valued logic, binding each operand that may be null once", (assert, helpers) => {
  const dueAfterIssue = op("le", [path("issuedOn", "date", true), path("dueOn", "date", true)], true);
  assert.equal(helpers.expr_cs(dueAfterIssue, invoice), "((IssuedOn, DueOn) switch { ({ } v1, { } v2) => (bool?)(v1 <= v2), _ => null })");
  assert.equal(helpers.expr_cs(op("and", [dueAfterIssue, op("ge", [path("total", "decimal", false), literal("0.50", "decimal")], false)], true), invoice),
    "(((IssuedOn, DueOn) switch { ({ } v1, { } v2) => (bool?)(v1 <= v2), _ => null }) & (bool?)(Total >= 0.50m))");
  assert.equal(helpers.expr_cs(op("or", [op("isNull", [path("dueOn", "date", true)], false), op("gt", [path("total", "decimal", false), literal(0, "int", 32)], false)], false), invoice),
    "((DueOn is null) || (Total > 0))");
  assert.equal(helpers.expr_cs(call("length", [path("note", "string", true)], true, "int"), invoice),
    "(Note switch { { } v1 => (int?)System.Linq.Enumerable.Count(v1.EnumerateRunes()), _ => null })");
});

maquettiste.test("expr_cs never writes an operand twice, however deep the nesting", (assert, helpers) => {
  // length(trim(trim(note))) <= 5: each level binds the one below once, so the text grows with the depth, not twice per level.
  let tree = path("note", "string", true);
  for (let i = 0; i < 2; i++) tree = call("trim", [tree], true, "string");
  const cs = helpers.expr_cs(op("le", [call("length", [tree], true, "int"), literal(5, "int", 32)], true), invoice);
  assert.equal(cs, "((((Note switch { { } v1 => (string?)v1.Trim(' '), _ => null }) switch { { } v2 => (string?)v2.Trim(' '), _ => null }) switch "
    + "{ { } v3 => (int?)System.Linq.Enumerable.Count(v3.EnumerateRunes()), _ => null }) switch { { } v4 => (bool?)(v4 <= 5), _ => null })");
  assert.equal(cs.split("Note").length - 1, 1);
  // x between low and high reads x once: bound when it is computed.
  assert.equal(helpers.expr_cs(op("between", [op("mul", [path("qty", "int", true), literal(2, "int", 32)], true, "int", 64), literal(0, "int", 32), literal(10, "int", 32)], true), invoice),
    "(((System.Numerics.BigInteger?)Qty * 2) switch { { } v1 => (bool?)((0 <= v1) && (v1 <= 10)), _ => null })");
});

maquettiste.test("expr_cs reads a guard's context and payload, a missing payload member being null", (assert, helpers) => {
  const guard = "total|call.Context.Total|decimal|value|;event.amount|call.Event.Get<decimal?>(\"amount\")|decimal?|value|";
  assert.equal(helpers.expr_cs(op("ge", [path("event.amount", "decimal", true), path("total", "decimal", true)], true), guard),
    "(call.Event.Get<decimal?>(\"amount\") switch { { } v1 => (bool?)(v1 >= call.Context.Total), _ => null })");
});

maquettiste.test("expr_cs_holds is true only when the condition is, and false for one that is always null", (assert, helpers) => {
  const guard = "total|call.Context.Total|decimal|value|;limit|call.Context.Limit|decimal|value|";
  assert.equal(helpers.expr_cs_holds(op("gt", [path("total", "decimal", false), path("limit", "decimal", false)], false), guard),
    "(call.Context.Total > call.Context.Limit) == true");
  // total > null is null whatever total is: C# refuses (bool?)null == true (CS0472), and the guard never holds.
  assert.equal(helpers.expr_cs_holds(op("gt", [path("total", "decimal", false), { node: "null", type: { kind: "decimal", bits: 0 }, nullable: true }], true), guard),
    "false");
  assert.equal(helpers.expr_cs_holds(null, guard), "");
});

maquettiste.test("expr_cs knows a value against itself, which C# warns of comparing", (assert, helpers) => {
  assert.equal(helpers.expr_cs(op("le", [path("total", "decimal", false), path("total", "decimal", false)], false), invoice), "true");
  assert.equal(helpers.expr_cs(op("lt", [path("total", "decimal", false), path("total", "decimal", false)], false), invoice), "false");
  assert.equal(helpers.expr_cs(op("notDistinct", [path("dueOn", "date", true), path("dueOn", "date", true)], false), invoice), "true");
  // One that may be null is unknown when it is null, so it is compared, bound twice.
  assert.equal(helpers.expr_cs(op("le", [path("dueOn", "date", true), path("dueOn", "date", true)], true), invoice),
    "((DueOn, DueOn) switch { ({ } v1, { } v2) => (bool?)(v1 <= v2), _ => null })");
});

maquettiste.test("expr_cs compares enum members by their C# names, and strings ordinally for equality and by code point for order", (assert, helpers) => {
  assert.equal(helpers.expr_cs(op("eq", [path("status", "enum", false), ref("Paid")], false), invoice), "(Status == InvoiceStatus.Paid)");
  assert.equal(helpers.expr_cs(op("in", [path("status", "enum", false), ref("Draft"), ref("Paid")], false), invoice),
    "((Status == InvoiceStatus.Draft) || (Status == InvoiceStatus.Paid))");
  assert.equal(helpers.expr_cs(op("ne", [path("number", "string", false), literal("INV-5", "string")], false), invoice),
    "!string.Equals(Number, \"INV-5\", StringComparison.Ordinal)");
  assert.equal(helpers.expr_cs(op("lt", [path("number", "string", false), literal("INV-5", "string")], false), invoice), "(CodePoints.Compare(Number, \"INV-5\") < 0)");
  // Without a compare() entry there is no order of strings to call, so the pack falls back.
  assert.equal(helpers.expr_cs(op("lt", [path("number", "string", false), literal("INV-5", "string")], false), "number|Number|string|value|"), "");
});

maquettiste.test("expr_cs computes integers in BigInteger and writes literals from their exact text", (assert, helpers) => {
  // big + 1 > big: long would wrap at long.MaxValue, where PostgreSQL's numeric and the runtime's BigInt answer true.
  assert.equal(helpers.expr_cs(op("gt", [op("add", [path("big", "int", true, 64), literal(1, "int", 32)], true, "int", 64), path("big", "int", true, 64)], true), invoice),
    "((((System.Numerics.BigInteger?)Big + 1), Big) switch { ({ } v1, { } v2) => (bool?)(v1 > v2), _ => null })");
  assert.equal(helpers.expr_cs(op("le", [path("big", "int", true, 64), op("neg", [path("qty", "int", true)], true, "int", 64)], true), invoice),
    "((Big, (-(System.Numerics.BigInteger?)Qty)) switch { ({ } v1, { } v2) => (bool?)(v1 <= v2), _ => null })");
  // value is a double in a script, 9007199254740992 for 2^53 + 1; valueText keeps its digits.
  assert.equal(helpers.expr_cs(op("le", [path("big", "int", true, 64), literal(9007199254740992, "int", 64, "9007199254740993")], true), invoice),
    "(Big switch { { } v1 => (bool?)(v1 <= 9007199254740993L), _ => null })");
  assert.equal(helpers.expr_cs(op("lt", [path("big", "int", true, 64), literal(9223372036854775807, "int", 64, "9223372036854775807")], true), invoice),
    "(Big switch { { } v1 => (bool?)(v1 < 9223372036854775807L), _ => null })");
  // A decimal of more than 28 digits does not fit C#'s decimal, which would round it.
  assert.equal(helpers.expr_cs(op("gt", [path("total", "decimal", false), literal("0.12345678901234567890123456789", "decimal")], false), invoice), "");
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

maquettiste.test("cs_unwrap drops the parentheses around a whole expression only", (assert, helpers) => {
  assert.equal(helpers.cs_unwrap("(Total >= 0)"), "Total >= 0");
  assert.equal(helpers.cs_unwrap("(A) & (B)"), "(A) & (B)");
  assert.equal(helpers.cs_unwrap("(Code == \")\")"), "Code == \")\"");
  assert.equal(helpers.cs_unwrap("Active"), "Active");
});

maquettiste.test("cs_line keeps a comment on one line", (assert, helpers) => {
  assert.equal(helpers.cs_line("Sends the invoice\r\nand records it\nonce."), "Sends the invoice and records it once.");
  assert.equal(helpers.cs_line(null), "");
});
