maquettiste.test("record_name adds the suffix", (assert, helpers) => {
  assert.equal(helpers.record_name({ name: "invoice" }), "InvoiceRecord");
});
maquettiste.test("record_name keeps acronyms", (assert, helpers) => {
  assert.equal(helpers.record_name({ name: "VATRate" }), "VATRateRecord");
});
