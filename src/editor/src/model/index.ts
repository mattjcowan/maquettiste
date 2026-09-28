import type { ElementKind, ElementSummary } from "@/api/types";

/** Lookups over the model index (every element's summary). */
export interface IndexLookup {
  rows: ElementSummary[];
  byId: Map<string, ElementSummary>;
  ofKind(kind: ElementKind): ElementSummary[];
  nameOf(id: string): string | undefined;
}

export function indexLookup(rows: ElementSummary[] | undefined): IndexLookup {
  const list = rows ?? [];
  const byId = new Map(list.map((r) => [r.id, r]));
  const byKind = new Map<ElementKind, ElementSummary[]>();
  for (const row of list) {
    const bucket = byKind.get(row.kind) ?? [];
    bucket.push(row);
    byKind.set(row.kind, bucket);
  }
  for (const bucket of byKind.values()) bucket.sort((a, b) => a.name.localeCompare(b.name));
  return {
    rows: list,
    byId,
    ofKind: (kind) => byKind.get(kind) ?? [],
    nameOf: (id) => byId.get(id)?.name,
  };
}

/** Category colors cycle through the eight categorical tokens in category-tree order. */
export function categoryColorIndex(categoryIds: string[], id: string | null | undefined): number | null {
  if (!id) return null;
  const at = categoryIds.indexOf(id);
  return at < 0 ? null : (at % 8) + 1;
}
