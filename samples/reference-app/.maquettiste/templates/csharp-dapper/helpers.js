// Helpers for the csharp-dapper pack. They run in the Maquettiste sandbox: no CLR, no files, no clock. Every helper is a pure
// function of its arguments (D12), so output does not depend on the order units render in.

// Joins path segments with "/", skipping empty ones, so an empty folder parameter adds nothing.
maquettiste.helper("join_path", (...parts) =>
  parts.filter((p) => p !== null && p !== undefined && String(p) !== "").map((p) => String(p)).join("/"));

// The folder of an element's package ("Billing/Catalog" for package Catalog inside Billing); "" at the root.
maquettiste.helper("package_folder", (element) =>
  element.package ? element.package.qualifiedName.split(".").join("/") : "");

const keywords = new Set([
  "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
  "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
  "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
  "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref",
  "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
  "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
]);

// A C# identifier: keywords get the verbatim "@" prefix ("class" -> "@class").
maquettiste.helper("cs_ident", (name) => (keywords.has(name) ? "@" + name : name));

const valueTypes = new Set([
  "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char",
  "DateOnly", "TimeOnly", "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "JsonElement",
]);

// Whether a C# type name from types/csharp.json is a value type (so it needs no "= default!" initializer).
maquettiste.helper("cs_value_type", (type) => valueTypes.has(String(type)));
