// The keys of a query file that 0.28.0 removed (the engine's Json/RemovedQueryKeys.cs): `entity`, `collections` and a select
// field's `attribute`. A file that still has one gets one MQ4127 error at the first such key, naming the replacement, in place of
// the schema violations the keys cause; the file does not load, and no setting changes the error.
type Json = Record<string, unknown>;

/** The rule of a removed key. */
export const REMOVED_QUERY_KEYS_RULE = "MQ4127";

const escape = (name: string) => name.replaceAll("~", "~0").replaceAll("/", "~1");

/** The pointers of the removed keys: `/entity`, `/collections`, then each select field's `attribute` in document order. */
export function removedQueryKeys(doc: Json): string[] {
  const found: string[] = [];
  if ("entity" in doc) found.push("/entity");
  if ("collections" in doc) found.push("/collections");
  const walk = (value: unknown, pointer: string, inSelect: boolean) => {
    if (Array.isArray(value))
      value.forEach((item, i) => {
        const at = `${pointer}/${i}`;
        if (inSelect && item && typeof item === "object" && !Array.isArray(item) && "attribute" in item) found.push(`${at}/attribute`);
        walk(item, at, false);
      });
    else if (value && typeof value === "object")
      for (const [key, child] of Object.entries(value)) if (key !== "sql") walk(child, `${pointer}/${escape(key)}`, key === "select");
  };
  for (const [key, value] of Object.entries(doc))
    if (!["entity", "collections", "properties", "generation", "source"].includes(key)) walk(value, `/${escape(key)}`, key === "select");
  return found;
}

/** The one finding's message, at `pointers[0]`, as the engine words it. */
export function removedQueryKeysMessage(doc: Json, pointers: readonly string[]): string {
  const what: string[] = [];
  if (pointers.includes("/entity")) what.push("an entity as the shape of its rows (entity)");
  if (pointers.includes("/collections")) what.push("collections");
  const fields = pointers.filter((p) => p.endsWith("/attribute")).length;
  if (fields > 0) what.push(fields === 1 ? "a select field that fills an attribute (attribute)" : `${fields} select fields that fill attributes (attribute)`);
  const listed = what.length === 1 ? what[0] : `${what.slice(0, -1).join(", ")} and ${what[what.length - 1]}`;
  const name = typeof doc.name === "string" ? `'${doc.name}' ` : "";
  return (
    `${pointers[0]} Query ${name}still has ${listed}, which were removed in 0.28.0: a query names no entity. ` +
    "Give each select field a name of its own and bind the entity to the query (its binding's source names the query, each binding field a column); " +
    "read a record with its parts through composition (entity.parts), each part with its own binding."
  );
}

/** Whether a schema failure at `pointer` is one the removed keys cause (at or under a removed key, or a field that has `attribute`). */
export function coveredByRemovedKeys(pointer: string, removed: readonly string[]): boolean {
  return removed.some((r) => pointer === r || pointer.startsWith(r + "/") || (r.endsWith("/attribute") && pointer === r.slice(0, -"/attribute".length)));
}

/** Where a schema failure is: Ajv reports an extra property at its parent, the engine's evaluator at the property itself. */
export function failurePointer(e: { instancePath: string; keyword: string; params?: Record<string, unknown> }): string {
  const extra = e.params?.additionalProperty;
  return e.keyword === "additionalProperties" && typeof extra === "string" ? `${e.instancePath}/${escape(extra)}` : e.instancePath;
}
