// Pack scripts for the end-to-end fixture. Helpers must be pure (D12).
maquettiste.helper("shout", (text) => String(text).toUpperCase() + "!");
maquettiste.selector("audited", (model) => model.entities.filter((e) => e.stereotypes.some((s) => s.key === "audited")).map((e) => e.id));
