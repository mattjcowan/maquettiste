// Tests of the helpers in helpers.js. `maquettiste pack test sql-ddl` runs them after the billing-small case; each test gets the
// assert API and the pack's helpers, and maquettiste.params holds the pack's default parameters.

// The foreign-key graph is the text dependency_spec builds: one entry per table separated by ";", each the indexes of the tables
// its foreign keys reference ("x" for one outside the list). Here invoices (0) references customers (1), and invoice_lines (2)
// references invoices.
maquettiste.test("ddl_order puts each table after the tables it references", (assert, helpers) => {
  assert.deepEqual(helpers.ddl_order("1;;0"), [1, 0, 2]);
  assert.deepEqual(helpers.ddl_order("x;0"), [0, 1]);
  assert.deepEqual(helpers.ddl_order(""), []);
});

maquettiste.test("ddl_cycle_breaks names the foreign keys that close a cycle", (assert, helpers) => {
  assert.deepEqual(helpers.ddl_cycle_breaks("1;;0"), []);
  // invoices (0) references customers (1), and customers references invoices back through its second foreign key.
  assert.deepEqual(helpers.ddl_cycle_breaks("1;x,0"), ["1:1"]);
});

maquettiste.test("reference_realization maps a strategy key through strategyMap", (assert, helpers) => {
  const map = maquettiste.params.strategyMap;
  assert.equal(helpers.reference_realization("lookup-table", map), "lookup-table");
  assert.equal(helpers.reference_realization("check", map), "check");
  assert.equal(helpers.reference_realization("compact", { compact: "native" }), "native");
});

maquettiste.test("reference_realization refuses an unknown key or realization", (assert, helpers) => {
  const unknown = assert.throws(() => helpers.reference_realization("compact", maquettiste.params.strategyMap));
  assert.ok(unknown.includes("'compact' is not in the pack's strategyMap parameter"), unknown);
  const realization = assert.throws(() => helpers.reference_realization("compact", { compact: "table" }));
  assert.ok(realization.includes("maps 'compact' to 'table'"), realization);
});
