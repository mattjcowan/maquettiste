// What may hold a lifecycle's state (lifecycle-design.md decision 11), shared by the bound-attribute picker and the
// mock's rules: an attribute of the subject, own, inherited or from a stereotype, that is single-valued and typed by a
// non-flags enum, a reference type, or a plain status (string, text, int16, int32, int64, directly or by a scalar type).
type Json = Record<string, unknown>;

export type BindingKind = "enum" | "reference" | "plain";

export type BindingType =
  | { kind: BindingKind; typeId?: string }
  | { kind: "unsupported"; why: string }
  /** A type that does not resolve (MQ2001 reports it): no binding finding. */
  | { kind: "unknown" };

export const PLAIN_KEYWORDS: ReadonlySet<string> = new Set(["string", "text", "int16", "int32", "int64"]);

/** How an attribute holds a state, or why it cannot. `doc` reads an element by id. */
export function classifyAttribute(attribute: Json, doc: (id: string) => Json | undefined): BindingType {
  if (attribute.collection === true) return { kind: "unsupported", why: "is a collection" };
  const type = attribute.type;
  if (typeof type === "string") {
    return PLAIN_KEYWORDS.has(type) ? { kind: "plain" } : { kind: "unsupported", why: `is typed by ${type}, which cannot hold a state` };
  }
  const ref = type && typeof type === "object" ? (type as Json).ref : undefined;
  if (typeof ref !== "string") return { kind: "unknown" };
  const target = doc(ref);
  if (!target) return { kind: "unknown" };
  switch (target.kind) {
    case "enum":
      return target.flags === true
        ? { kind: "unsupported", why: `is typed by the flags enum '${String(target.name)}', which holds several members at once` }
        : { kind: "enum", typeId: ref };
    case "reference-type":
      return { kind: "reference", typeId: ref };
    case "scalar-type":
      return typeof target.base === "string" && PLAIN_KEYWORDS.has(target.base)
        ? { kind: "plain", typeId: ref }
        : { kind: "unsupported", why: `is typed by scalar type '${String(target.name)}', which cannot hold a state` };
    default:
      return { kind: "unsupported", why: `is typed by ${String(target.kind)} '${String(target.name)}', which cannot hold a state` };
  }
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

/**
 * The attributes of an entity as a lifecycle may bind them: its own, its bases' (nearest first) and its stereotypes'
 * (those of the entity and its bases). `stereotype` reads a stereotype by key.
 */
export function bindableAttributes(entity: Json | undefined, doc: (id: string) => Json | undefined, stereotype: (key: string) => Json | undefined): Json[] {
  const out: Json[] = [];
  const seen = new Set<string>();
  for (let at = entity; at && !seen.has(String(at.id)); at = typeof at.base === "string" ? doc(at.base) : undefined) {
    seen.add(String(at.id));
    out.push(...arr(at.attributes));
    for (const key of (at.stereotypes as string[] | undefined) ?? []) out.push(...arr(stereotype(key)?.attributes));
  }
  return out;
}

/**
 * The form a state's code takes under a binding: "string" for a string or text status and for a reference type whose
 * codes are strings, "integer" for int16/int32/int64 and integer-coded reference types, and, for an enum, its
 * members' integer values (the lifecycle's own codes decide first). Undefined when the type cannot tell.
 */
export function codeKindOf(
  type: BindingType,
  attribute: Json,
  doc: (id: string) => Json | undefined,
): "string" | "integer" | { enumValues: number[] } | undefined {
  const integerKeyword = (k: unknown) => typeof k === "string" && /^int(16|32|64)$/.test(k);
  switch (type.kind) {
    case "plain": {
      const keyword = typeof attribute.type === "string" ? attribute.type : (doc(type.typeId ?? "")?.base as string | undefined);
      return keyword ? (integerKeyword(keyword) ? "integer" : "string") : undefined;
    }
    case "reference": {
      const code = doc(type.typeId ?? "")?.code as Json | undefined;
      return typeof code?.type === "string" ? (integerKeyword(code.type) ? "integer" : "string") : undefined;
    }
    case "enum": {
      const members = arr(doc(type.typeId ?? "")?.members);
      return { enumValues: members.map((m) => m.value).filter((v): v is number => typeof v === "number") };
    }
    default:
      return undefined;
  }
}
