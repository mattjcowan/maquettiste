// Fanout pack helpers (bench only). They run in the Jint sandbox for most units, so the sandbox is on the benchmark's hot path.
// Helpers must be pure (D12).

const tsBuiltins = {
  string: "string", text: "string", uuid: "string", ulid: "string", json: "unknown", binary: "Uint8Array",
  bool: "boolean", int16: "number", int32: "number", int64: "bigint", decimal: "string", float: "number", double: "number",
  date: "string", time: "string", datetime: "string", datetimeoffset: "string", duration: "string",
};

function tsType(attribute) {
  const type = attribute.type;
  if (type.kind === "builtin") return tsBuiltins[type.builtin] || "unknown";
  if (type.kind === "scalar") return tsBuiltins[type.builtin] || "string";
  return type.name;
}

function kebab(name) {
  return name.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
}

// One TypeScript field declaration per attribute.
maquettiste.helper("ts_fields", (entity) =>
  entity.attributes.map((a) => `${a.name}${a.required ? "" : "?"}: ${tsType(a)};`));

// A REST route for an entity.
maquettiste.helper("route", (entity) => `/api/${kebab(entity.package.name)}/${kebab(entity.name)}`);

// A one-line summary of an entity.
maquettiste.helper("doc_summary", (entity) => {
  const required = entity.attributes.filter((a) => a.required).length;
  return `${entity.displayName} has ${entity.attributes.length} attributes (${required} required) and ${entity.navigations.length} navigations.`;
});
