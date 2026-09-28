maquettiste.rule({
  id: "upper-case",
  severity: "info",
  kinds: ["entity"],
  check(element, model, report) {
    if (element.name === "Boom") throw new Error("boom");
    if (element.name === "Loop") { for (;;) {} }
  },
});
