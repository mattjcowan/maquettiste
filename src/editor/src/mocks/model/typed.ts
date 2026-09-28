// The engine's typed record (`ElementDocument.element`): the model document with every member the
// kind's schema declares present, defaults and nulls written.
import { openapi } from "../contract";

type Schema = Record<string, unknown>;
const components = (openapi as { components: { schemas: Record<string, Schema> } }).components.schemas;

function resolve(schema: Schema | undefined): Schema | undefined {
  let current = schema;
  for (let i = 0; current && typeof current.$ref === "string" && i < 20; i++) {
    const name = (current.$ref as string).replace("#/components/schemas/", "");
    current = components[name];
  }
  return current;
}

function fill(value: unknown, schema: Schema | undefined): unknown {
  const resolved = resolve(schema);
  if (value === null || value === undefined || !resolved) return value;
  if (Array.isArray(value)) {
    const items = resolved.items as Schema | undefined;
    return value.map((v) => fill(v, items));
  }
  if (typeof value === "object" && resolved.properties && typeof resolved.properties === "object") {
    const properties = resolved.properties as Record<string, Schema>;
    const out: Record<string, unknown> = {};
    for (const [name, propertySchema] of Object.entries(properties)) {
      const has = Object.hasOwn(value as object, name);
      if (has) out[name] = fill((value as Record<string, unknown>)[name], propertySchema);
      else {
        const withDefault = "default" in propertySchema ? propertySchema : resolve(propertySchema);
        out[name] = withDefault && "default" in withDefault ? structuredClone(withDefault.default) : null;
      }
    }
    for (const [name, v] of Object.entries(value as Record<string, unknown>)) if (!(name in out)) out[name] = v;
    return out;
  }
  return value;
}

export function typedElement(json: Record<string, unknown>): Record<string, unknown> {
  const kind = String(json.kind);
  const filled = fill(json, components[kind]) as Record<string, unknown>;
  if (filled.$schema === undefined) filled.$schema = null;
  return filled;
}
