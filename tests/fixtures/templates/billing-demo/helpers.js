// Pack helpers for the W5 fixture. Helpers must be pure (D12).
maquettiste.helper("shout", (text) => String(text).toUpperCase() + "!");

// Reads through the model proxy, so the entity's dependencies are recorded by the sandbox.
maquettiste.helper("attribute_names", (entity) => entity.attributes.map((a) => a.name).join(", "));

// Returns a plain object; templates see it as a map in ordinal key order.
maquettiste.helper("describe", (entity) => ({ zeta: entity.attributes.length, alpha: entity.name, list: [1, 2, 3] }));

maquettiste.transform("summary", (entity) => ({
  attributeCount: entity.attributes.length,
  requiredNames: entity.attributes.filter((a) => a.required).map((a) => a.name),
  prefix: maquettiste.params.namespace,
}));
