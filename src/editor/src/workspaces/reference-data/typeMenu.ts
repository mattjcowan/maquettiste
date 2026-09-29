// The reference type menu (reference-types-seeds-localization.md 4.2), as data and pure edits: which actions a type,
// or several, offer; the copy Duplicate makes; whether Convert to enum applies and the enum it makes. TypeMenu.tsx
// renders the menu and runs the actions; the explorer's Reference data rows offer the same items.
import type { EnumDoc, ReferenceTypeDoc, SeedDoc } from "@/api/types";
import { IDENTIFIER } from "@/model/model";

export type TypeActionId = "duplicate" | "rename" | "move-category" | "set-storage" | "export-csv" | "convert-to-enum" | "delete";

export interface TypeMenuItem {
  id: TypeActionId;
  label: string;
  /** Offered for several types at once. */
  multi: boolean;
  danger?: boolean;
}

export const TYPE_MENU: readonly TypeMenuItem[] = [
  { id: "duplicate", label: "Duplicate", multi: false },
  { id: "rename", label: "Rename", multi: false },
  { id: "move-category", label: "Move to category…", multi: true },
  { id: "set-storage", label: "Set storage…", multi: false },
  { id: "export-csv", label: "Export CSV", multi: false },
  { id: "convert-to-enum", label: "Convert to enum…", multi: false },
  { id: "delete", label: "Delete…", multi: true, danger: true },
];

export const isTypeAction = (id: string): id is TypeActionId => TYPE_MENU.some((i) => i.id === id);

/** The items for a selection of types: every item for one, the multi ones for several; Export CSV needs a seed. */
export function typeMenuFor(targets: readonly { seeds: readonly string[] }[]): TypeMenuItem[] {
  if (!targets.length) return [];
  const items = targets.length === 1 ? [...TYPE_MENU] : TYPE_MENU.filter((i) => i.multi);
  return items.filter((i) => i.id !== "export-csv" || targets[0].seeds.length > 0);
}

/** The lowest free "<name>Copy", "<name>Copy2", … among the taken names (ignoring case: names map to files). */
export function copyName(name: string, taken: Iterable<string>): string {
  const used = new Set([...taken].map((t) => t.toLowerCase()));
  let n = 1;
  const at = (i: number) => `${name}Copy${i === 1 ? "" : i}`;
  while (used.has(at(n).toLowerCase())) n++;
  return at(n);
}

/**
 * Duplicate: the type under a new id and name with new field ids, and each of its seeds retargeted with new row ids
 * (row ids are model-wide identities). The seed named after the type takes the new name; the others keep theirs.
 */
export function duplicateType(
  type: ReferenceTypeDoc,
  seeds: readonly SeedDoc[],
  name: string,
  newId: () => string,
): { type: ReferenceTypeDoc; seeds: SeedDoc[] } {
  const copy = structuredClone(type);
  copy.id = newId();
  copy.name = name;
  copy.code = { ...copy.code, id: newId() };
  copy.label = { ...copy.label, id: newId() };
  if (copy.attributes) copy.attributes = copy.attributes.map((a) => ({ ...a, id: newId() }));
  const seedCopies = seeds.map((s) => {
    const c = structuredClone(s);
    c.id = newId();
    c.target = copy.id;
    if (s.name === type.name) c.name = name;
    if (c.rows) c.rows = c.rows.map((r) => ({ ...r, id: newId() }));
    return c;
  });
  return { type: copy, seeds: seedCopies };
}

/** The codes and labels of a type's rows, across its seeds, in seed then row order. */
export function typeRows(seeds: readonly SeedDoc[]): { code: unknown; label: unknown }[] {
  return seeds.flatMap((s) => {
    const code = s.columns.indexOf("code");
    const label = s.columns.indexOf("label");
    return (s.rows ?? []).map((r) => ({ code: code >= 0 ? r.values?.[code] : undefined, label: label >= 0 ? r.values?.[label] : undefined }));
  });
}

/**
 * Why Convert to enum does not apply, or null when it does (RT 2: back from a reference type when it has no fields of
 * its own and its codes are identifiers): the members are named after the codes.
 */
export function enumConversionProblem(type: ReferenceTypeDoc, seeds: readonly SeedDoc[]): string | null {
  if (type.attributes?.length) return `${type.name} has fields of its own; an enum member has only a name and a display name.`;
  const codes = typeRows(seeds).map((r) => r.code);
  const bad = codes.find((c) => typeof c !== "string" || !IDENTIFIER.test(c));
  if (bad !== undefined) return `The code ${JSON.stringify(bad)} is not an identifier, so it cannot name an enum member.`;
  const seen = new Set<string>();
  for (const c of codes as string[]) {
    if (seen.has(c)) return `The code ${c} appears twice.`;
    seen.add(c);
  }
  return null;
}

/** The enum Convert to enum makes: the type's name, display name, description and marks; one member per row. */
export function enumFromType(type: ReferenceTypeDoc, seeds: readonly SeedDoc[], newId: () => string): EnumDoc {
  const members = typeRows(seeds).map((r) => ({
    id: newId(),
    name: String(r.code),
    ...(typeof r.label === "string" && r.label !== r.code ? { displayName: r.label } : {}),
  }));
  return {
    kind: "enum",
    id: newId(),
    name: type.name,
    ...(type.displayName ? { displayName: type.displayName } : {}),
    ...(type.pluralName ? { pluralName: type.pluralName } : {}),
    ...(type.description ? { description: type.description } : {}),
    ...(type.stereotypes?.length ? { stereotypes: type.stereotypes } : {}),
    ...(type.tags?.length ? { tags: type.tags } : {}),
    ...(type.category ? { category: type.category } : {}),
    members,
  } as EnumDoc;
}

/** Points every attribute of an element (and of nested attribute lists) typed `{ ref: from }` at `to`; true when any changed. */
export function retypeAttributes(json: Record<string, unknown>, from: string, to: string): boolean {
  let changed = false;
  const walk = (value: unknown) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (value && typeof value === "object") {
      const rec = value as Record<string, unknown>;
      if (Array.isArray(rec.attributes))
        for (const a of rec.attributes as Record<string, unknown>[]) {
          if (a && typeof a.type === "object" && (a.type as { ref?: string } | null)?.ref === from) {
            a.type = { ref: to };
            changed = true;
          }
        }
      for (const [k, v] of Object.entries(rec)) if (k !== "attributes" && typeof v === "object") walk(v);
    }
  };
  walk(json);
  return changed;
}
