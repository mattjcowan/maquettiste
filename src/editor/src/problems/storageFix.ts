// The quick fixes of the storage findings for relations with an end of several types (several-types-design.md 19.10). The derivation is pure: it
// reads the finding, its element and (for MQ4009) the relation the mapping is for. The few that change no document of their own (open the
// Storage tab, copy the check) and the two that edit a mapping or a binding found by name (Not in this database, Keep the stored code) are
// carried out by `applyStorageFix`.
import type { Diagnostic, ModelJson } from "@/api/types";
import { clone } from "@/lib/json";

type Json = Record<string, unknown>;

export type StorageFix =
  /** Opens the relation's Storage tab. */
  | { kind: "storage"; action: "open"; relation: string; label: string }
  /** MQ4115: the relation's mapping in the database says `ignore`, creating the mapping when there is none. */
  | { kind: "storage"; action: "ignore"; relation: string; database: string; label: string }
  /** MQ4118: puts the text to copy on the clipboard. */
  | { kind: "storage"; action: "copy"; text: string; label: string }
  /** MQ4119: the old code becomes the `discriminatorValue` of the entity's binding or mapping in the database (decision 52). */
  | { kind: "storage"; action: "keep-code"; entity: string; database: string; code: string; label: string };

const str = (v: unknown): string | undefined => (typeof v === "string" && v ? v : undefined);

/** The rules this file gives a fix to. */
export const STORAGE_FIX_RULES = ["MQ4009", "MQ4115", "MQ4118", "MQ4119", "MQ4120"] as const;

/** The text to copy out of an MQ4118 message: every "To …, add: <text>" it holds, one per line. */
export function copyTextOf(message: string): string | null {
  const parts = message.split(/ ?To [^,]+, add: /).slice(1);
  return parts.length ? parts.map((p) => p.trim()).join("\n") : null;
}

/** The entity, database and old code an MQ4119 message names. */
export function keptCodeOf(message: string): { entity: string; database: string; code: string } | null {
  const at = /type code of '([^']+)' in database '([^']+)' changed from '([^']*)' to/.exec(message);
  return at ? { entity: at[1], database: at[2], code: at[3] } : null;
}

const KEYS_REFUSED = new Set(["foreignKeyEnd", "foreignKey", "promotedName", "shape", "attributes", "constants"]);

/**
 * MQ4009 on a relation mapping with an end of several types: the key goes (its pointer is `/<key>`), or a top-level `foreignKey` moves to the
 * entry of an end that has none (the end the dependent end `foreignKeyEnd` doesn't name, else the first end with no entry), as the way that
 * end is pointed at. `relation` is the mapping's relation document.
 */
export function mappingKeyFix(mapping: ModelJson, relation: ModelJson | undefined, pointer: string): { json: ModelJson; label: string } | null {
  const key = /^\/([A-Za-z]+)$/.exec(pointer)?.[1];
  if (!key || !KEYS_REFUSED.has(key) || mapping.kind !== "mapping" || (mapping as Json)[key] === undefined) return null;
  const next = clone(mapping) as Json;
  if (key === "foreignKey" && relation) {
    const ends = ((relation as Json).ends as Json[] | undefined) ?? [];
    const entries = (next.ends as Json[] | undefined) ?? [];
    const lacking = ends.filter((e) => !entries.some((x) => x.end === e.id));
    const dependent = str(next.foreignKeyEnd);
    const target = lacking.find((e) => e.id !== dependent) ?? lacking[0];
    if (target && typeof next.foreignKey === "string") {
      entries.push({ end: target.id, foreignKey: next.foreignKey });
      next.ends = entries;
      delete next.foreignKey;
      delete next.foreignKeyEnd;
      return { json: next as ModelJson, label: "Move it to the end's entry" };
    }
  }
  delete next[key];
  return { json: next as ModelJson, label: "Remove the key" };
}

/**
 * The main fix of a storage finding, or null. `element` is the finding's document (the relation for MQ4115, the mapping for the others) and
 * `relation` the mapping's relation, for MQ4009's move.
 */
export function storageFix(
  d: Diagnostic,
  element: ModelJson,
  relation?: ModelJson,
): StorageFix | { kind: "update"; id: string; json: ModelJson; label: string } | null {
  const id = str(element.id) ?? d.elementId ?? "";
  if (d.rule === "MQ4115") {
    const database = /database '([^']+)'/.exec(d.message)?.[1];
    return element.kind === "relation" && database ? { kind: "storage", action: "ignore", relation: id, database, label: "Not in this database" } : null;
  }
  if (d.rule === "MQ4120") {
    const rel = element.kind === "mapping" ? str((element as Json).relation) : undefined;
    return rel ? { kind: "storage", action: "open", relation: rel, label: "Open the Storage tab" } : null;
  }
  if (d.rule === "MQ4118") {
    const text = copyTextOf(d.message);
    return text ? { kind: "storage", action: "copy", text, label: "Copy the check" } : null;
  }
  if (d.rule === "MQ4119") {
    const kept = keptCodeOf(d.message);
    return kept ? { kind: "storage", action: "keep-code", ...kept, label: "Keep the stored code" } : null;
  }
  if (d.rule === "MQ4009") {
    const fix = mappingKeyFix(element, relation, d.jsonPointer ?? "");
    return fix ? { kind: "update", id, json: fix.json, label: fix.label } : null;
  }
  return null;
}

/** The second fix of MQ4115: open the Storage tab. */
export function storageExtras(d: Diagnostic, element: ModelJson): StorageFix[] {
  const id = str(element.id);
  return d.rule === "MQ4115" && element.kind === "relation" && id ? [{ kind: "storage", action: "open", relation: id, label: "Open the Storage tab" }] : [];
}

/** The relation document an MQ4009 finding on a mapping needs: the one the mapping is for. */
export function relationOfMapping(d: Diagnostic, element: ModelJson | undefined): string | undefined {
  return d.rule === "MQ4009" && element?.kind === "mapping" ? str((element as Json).relation) : undefined;
}
