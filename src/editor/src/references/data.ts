// The References tab's data (explorer-redesign.md 3.3): where an element is used. The index's related fields (relation
// ends, `base`, `package`, `entity`, `database`) answer at once and without a request; the server's
// `GET /api/model/references/{id}` then gives the complete list with exact fields, and replaces it. The rows are grouped
// by the kind of the referencing element, then by its domain, and flattened for a virtualized list.
import type { ElementSummary, ReferenceInfo } from "@/api/types";
import { GROUP_LABELS, kindFolder, OTHER_FOLDER } from "@/model/labels";

/** The references to `target` that the index rows show, in the server's shape. */
export function indexReferences(target: string, rows: readonly ElementSummary[]): ReferenceInfo[] {
  const out: ReferenceInfo[] = [];
  const add = (row: ElementSummary, jsonPointer: string, field: string) =>
    out.push({ fromElementId: row.id, fromId: row.id, jsonPointer, field, toId: target });
  for (const row of rows) {
    if (row.id === target) continue;
    if (row.package === target) add(row, row.kind === "package" ? "/parent" : "/package", row.kind === "package" ? "parent" : "package");
    if (row.base === target) add(row, "/base", "base");
    if (row.entity === target) add(row, "/entity", "entity");
    if (row.database === target) add(row, "/database", "database");
    row.ends?.forEach((end, i) => {
      if (end.entity === target) add(row, `/ends/${i}/entity`, "entity");
    });
  }
  return out;
}

export interface ReferenceRow {
  type: "reference";
  key: string;
  /** The element to go to, and the pointer to reveal in it. */
  elementId: string;
  pointer: string;
  label: string;
}
export interface ReferenceHeader {
  type: "kind" | "domain";
  key: string;
  label: string;
  count: number;
}
export type ReferenceListRow = ReferenceRow | ReferenceHeader;

/** A sub-element's name for a label, when the caller has the referencing document loaded. */
export type SubName = (elementId: string, collection: string, index: number) => string | undefined;

const singular = (word: string) => word.replace(/ies$/, "y").replace(/s$/, "");

/** "InvoiceLine · attribute amount → type", or "on diagram Billing overview" for a diagram member. */
export function referenceLabel(ref: ReferenceInfo, from: ElementSummary | undefined, subName?: SubName): string {
  const name = from?.name ?? ref.fromElementId;
  if (from?.kind === "diagram" && /^\/members\/\d+\/element$/.test(ref.jsonPointer)) return `on diagram ${name}`;
  const parts = ref.jsonPointer.split("/").slice(1);
  const at = parts.findIndex((p) => /^\d+$/.test(p));
  // A pointer into a collection (/attributes/3/type) names the item; a whole-array item (/packages/0) does not.
  if (at > 0 && at < parts.length - 1) {
    const collection = parts[at - 1];
    const index = Number(parts[at]);
    const sub = subName?.(ref.fromElementId, collection, index) ?? String(index + 1);
    return `${name} · ${singular(collection)} ${sub} → ${ref.field}`;
  }
  return `${name} → ${ref.field}`;
}

/** Groups by kind of the referencing element (in folder order), then by domain (Not in a domain last). */
export function groupReferences(refs: readonly ReferenceInfo[], byId: ReadonlyMap<string, ElementSummary>, subName?: SubName): ReferenceListRow[] {
  const seen = new Set<string>();
  const byKind = new Map<string, Map<string, ReferenceRow[]>>();
  for (const ref of refs) {
    const key = `${ref.fromId}|${ref.jsonPointer}`;
    if (seen.has(key)) continue;
    seen.add(key);
    const from = byId.get(ref.fromElementId);
    const kind = from?.kind ?? "";
    const domain = from?.package ?? null;
    const domainName = (domain && byId.get(domain)?.name) || GROUP_LABELS.notInDomain;
    const kindMap = byKind.get(kind) ?? new Map<string, ReferenceRow[]>();
    byKind.set(kind, kindMap);
    const list = kindMap.get(domainName) ?? [];
    kindMap.set(domainName, list);
    list.push({ type: "reference", key, elementId: ref.fromElementId, pointer: ref.jsonPointer, label: referenceLabel(ref, from, subName) });
  }
  const folderOf = (kind: string) => kindFolder(kind) ?? OTHER_FOLDER;
  const kinds = [...byKind.keys()].sort((a, b) => folderOf(a).order - folderOf(b).order || folderOf(a).label.localeCompare(folderOf(b).label));
  const out: ReferenceListRow[] = [];
  for (const kind of kinds) {
    const domains = byKind.get(kind)!;
    const count = [...domains.values()].reduce((n, l) => n + l.length, 0);
    out.push({ type: "kind", key: `kind:${kind}`, label: folderOf(kind).label, count });
    const names = [...domains.keys()].sort((a, b) => (a === GROUP_LABELS.notInDomain ? 1 : b === GROUP_LABELS.notInDomain ? -1 : a.localeCompare(b)));
    for (const name of names) {
      const list = domains.get(name)!.sort((a, b) => a.label.localeCompare(b.label));
      out.push({ type: "domain", key: `domain:${kind}:${name}`, label: name, count: list.length });
      out.push(...list);
    }
  }
  return out;
}

/** The number of references in a flattened list. */
export const referenceCount = (rows: readonly ReferenceListRow[]) => rows.filter((r) => r.type === "reference").length;
