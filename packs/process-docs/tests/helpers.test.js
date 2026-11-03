// Tests of the helpers in helpers.js. `maquettiste pack test process-docs` runs them after the billing-small case; each test gets
// the assert API and the pack's helpers.

maquettiste.test("md_cell escapes Markdown and keeps a cell on one line", (assert, helpers) => {
  assert.equal(helpers.md_cell("Invoice | total"), "Invoice \\| total");
  assert.equal(helpers.md_cell("Sent\nto *customer*"), "Sent to \\*customer\\*");
  assert.equal(helpers.md_cell(null), "");
});

maquettiste.test("md_code wraps text in a code span that is safe in a table", (assert, helpers) => {
  assert.equal(helpers.md_code("InvoiceLifecycle"), "`InvoiceLifecycle`");
  assert.equal(helpers.md_code("a | b"), "`a \\| b`");
  assert.equal(helpers.md_code("total `net`"), "`` total `net` ``");
  assert.equal(helpers.md_code(""), "");
});

maquettiste.test("diagram_text writes the diagram's syntax characters as entity codes", (assert, helpers) => {
  assert.equal(helpers.diagram_text("Due; \"late\" #2"), "Due#59; #quot;late#quot; #35;2");
  assert.equal(helpers.diagram_text("a < b\nc > d"), "a #lt; b c #gt; d");
});

maquettiste.test("diagram_reserved knows the diagram's keywords whatever their case", (assert, helpers) => {
  assert.ok(helpers.diagram_reserved("End"));
  assert.ok(helpers.diagram_reserved("note"));
  assert.ok(!helpers.diagram_reserved("Paid"));
  assert.ok(!helpers.diagram_reserved(null));
});
