maquettiste.rule({
  id: "no-draft",
  severity: "warning",
  kinds: ["entity"],
  check(element, model, report) {
    if ((element.tags || []).includes("draft")) report("Draft elements must not be generated.", { pointer: "/tags" });
  },
});
