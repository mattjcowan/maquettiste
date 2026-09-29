// Helpers for the typescript pack. They run in the Maquettiste sandbox (no files, no clock) and take plain strings, so they
// stay pure and cheap. Case helpers (pascal, camel, kebab) are the engine's built-ins and are not repeated here.

// The TypeScript type of a built-in type keyword. Values that lose precision as a JS number (decimal, int64) and dates travel
// as strings, the way JSON carries them.
const tsTypes = {
  bool: "boolean",
  int16: "number", int32: "number", float: "number", double: "number",
  int64: "string", decimal: "string",
  string: "string", text: "string", uuid: "string", ulid: "string",
  date: "string", time: "string", datetime: "string", datetimeoffset: "string", duration: "string",
  binary: "string", json: "unknown",
};
maquettiste.helper("ts_type", (builtin) => tsTypes[String(builtin)] ?? "unknown");

// The zod schema expression of a built-in type keyword; length is the attribute's maximum length (0 or null for none).
maquettiste.helper("zod_type", (builtin, length) => {
  const max = length ? `.max(${length})` : "";
  switch (String(builtin)) {
    case "bool": return "z.boolean()";
    case "int16": case "int32": return "z.number().int()";
    case "float": case "double": return "z.number()";
    case "int64": return "z.string().regex(/^-?\\d+$/)";
    case "decimal": return "z.string().regex(/^-?\\d+(\\.\\d+)?$/)";
    case "uuid": return "z.string().uuid()";
    case "ulid": return "z.string().ulid()";
    case "date": return "z.string().date()";
    case "time": return "z.string().time()";
    case "datetime": return "z.string().datetime({ local: true })";
    case "datetimeoffset": return "z.string().datetime({ offset: true })";
    case "duration": return "z.string().duration()";
    case "string": case "text": case "binary": return `z.string()${max}`;
    default: return "z.unknown()";
  }
});

// A single-quoted TypeScript string literal.
maquettiste.helper("ts_string", (text) => "'" + String(text).replace(/\\/g, "\\\\").replace(/'/g, "\\'") + "'");

// A relative import specifier for a generated module: "./order-line.js" (the extension comes from the importExtension parameter).
maquettiste.helper("ts_module", (fileName, extension) => "./" + fileName + (extension ?? ""));
