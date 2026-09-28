// The Mappings model (phase2-design.md 4.8), free of React: which columns of the entity's table
// each attribute produced (joined by attribute path, engine-design.md 7.3: `<attrId>` or
// `<attrId>.<memberAttrId>` for embedded value objects; attributeId only when a column has no
// path), whether a row follows the conventions or the mapping element overrides it, and the edits
// that create or change that mapping element.
import type { ColumnView, MappingAttributeDoc, MappingDoc, TableView } from "@/api/types";

export type RowState = "convention" | "override" | "ignored";

export interface MappingRow<A extends { id: string }> {
  attribute: A;
  columns: ColumnView[];
  override: MappingAttributeDoc | null;
  state: RowState;
}

/** True when the column was produced by the attribute (its own column or an embedded member's). */
export function columnOf(attributeId: string, column: Pick<ColumnView, "attributeId" | "attributePath">): boolean {
  const path = column.attributePath;
  if (path) return path === attributeId || path.startsWith(`${attributeId}.`);
  return column.attributeId === attributeId;
}

export function mappingRows<A extends { id: string }>(
  attributes: readonly A[],
  table: Pick<TableView, "columns"> | null,
  mapping: Pick<MappingDoc, "attributes"> | null,
): MappingRow<A>[] {
  return attributes.map((attribute) => {
    const override = mapping?.attributes?.find((o) => o.attribute === attribute.id) ?? null;
    const columns = table?.columns.filter((c) => columnOf(attribute.id, c)) ?? [];
    const state: RowState = override?.ignore ? "ignored" : override ? "override" : "convention";
    return { attribute, columns, override, state };
  });
}

/** The table's columns no attribute row accounts for: keys, foreign keys, discriminator, order. */
export function otherColumns(table: Pick<TableView, "columns"> | null, rows: readonly MappingRow<{ id: string }>[]): ColumnView[] {
  const used = new Set(rows.flatMap((r) => r.columns.map((c) => c.key)));
  return table?.columns.filter((c) => !used.has(c.key)) ?? [];
}

export type AttributePatch = Partial<Omit<MappingAttributeDoc, "attribute">>;

/**
 * Merges a patch into the attribute's override (in place). Empty values ("", false, undefined)
 * remove a field; an override left with only its `attribute` is removed, and so is an empty list.
 * A null patch removes the override.
 */
export function applyAttributeOverride(mapping: Pick<MappingDoc, "attributes">, attributeId: string, patch: AttributePatch | null): void {
  const list = [...(mapping.attributes ?? [])];
  const at = list.findIndex((a) => a.attribute === attributeId);
  const merged: Record<string, unknown> = { ...(at >= 0 ? list[at] : { attribute: attributeId }), ...(patch ?? {}) };
  for (const [k, v] of Object.entries(merged)) if (v === undefined || v === "" || v === false) delete merged[k];
  const keep = patch !== null && Object.keys(merged).length > 1;
  if (at >= 0) {
    if (keep) list[at] = merged as MappingAttributeDoc;
    else list.splice(at, 1);
  } else if (keep) list.push(merged as MappingAttributeDoc);
  if (list.length) mapping.attributes = list;
  else delete mapping.attributes;
}

export type EntityPatch = Partial<Pick<MappingDoc, "inheritance" | "ignore" | "table" | "discriminatorValue">>;

/** Sets or clears (on "", false, undefined) the entity-level mapping fields, in place. */
export function applyEntityOverride(mapping: MappingDoc, patch: EntityPatch): void {
  const target = mapping as Record<string, unknown>;
  for (const [k, v] of Object.entries(patch)) {
    if (v === undefined || v === "" || v === false) delete target[k];
    else target[k] = v;
  }
}

/** True when a patch would change nothing on a mapping that does not exist yet. */
export function isEmptyPatch(patch: AttributePatch | EntityPatch | null): boolean {
  return patch === null || Object.values(patch).every((v) => v === undefined || v === "" || v === false);
}

/** The storage choices an attribute of this referenced kind offers (none for builtins). */
export function storageOptions(kind: string | undefined): NonNullable<MappingAttributeDoc["storage"]>[] {
  if (kind === "enum") return ["int", "string", "lookup"];
  if (kind === "value-object") return ["embedded", "table", "json"];
  return [];
}

/**
 * One create per key (entity and database). The first edit on an unmapped entity creates the mapping;
 * edits made while that create is in flight wait for it and are then applied to the created mapping
 * instead of creating another. A key stays until `forget` (the mapping appears in the index) or the
 * create fails.
 */
export class PendingCreates {
  private readonly pending = new Map<string, Promise<string | null>>();

  async run(key: string, create: () => Promise<string | null>, editCreated: (id: string) => void | Promise<void>): Promise<void> {
    const inFlight = this.pending.get(key);
    if (inFlight) {
      const id = await inFlight;
      if (id) await editCreated(id);
      return;
    }
    const promise = create().then(
      (id) => {
        if (!id) this.pending.delete(key);
        return id;
      },
      (error: unknown) => {
        this.pending.delete(key);
        throw error;
      },
    );
    this.pending.set(key, promise);
    await promise;
  }

  has(key: string): boolean {
    return this.pending.has(key);
  }

  forget(key: string): void {
    this.pending.delete(key);
  }
}
