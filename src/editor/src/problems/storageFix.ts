// The quick fixes of the storage findings for relations with an end of several types (several-types-design.md 19.10). The derivation is pure: it
// reads the finding, its element and (for MQ4009) the relation the mapping is for. The few that change no document of their own (open the
// Storage tab, copy the check) and the two that edit a mapping or a binding found by name (Not in this database, Keep the stored code) are
// carried out by `applyStorageFix`.
import type { Diagnostic, ModelJson } from "@/api/types";
import { clone } from "@/lib/json";
import { docEndHasSeveralTypes } from "@/model/endTypes";

type Json = Record<string, unknown>;

export type StorageFix =
  /** Opens the relation's Storage tab. */
  | { kind: "storage"; action: "open"; relation: string; label: string }
  /** MQ4115: the relation's mapping in the database says `ignore`, creating the mapping when there is none. */
  | { kind: "storage"; action: "ignore"; relation: string; database: string; label: string }
  /** MQ4118: puts the text to copy on the clipboard. */
  | { kind: "storage"; action: "copy"; text: string; label: string }
  /** MQ4119: the old code becomes the `discriminatorValue` of the entity's binding or mapping in the database (decision 52). `entity` is its id. */
  | { kind: "storage"; action: "keep-code"; entity: string; database: string; code: string | number; label: string }
  /** MQ4009 on a top-level `foreignKey` of a relation with an end of several types: the key goes to the entry of the end whose table it refers to. */
  | { kind: "storage"; action: "move-key"; mapping: string; relation: string; key: string; label: string };

const str = (v: unknown): string | undefined => (typeof v === "string" && v ? v : undefined);

/** The rules this file gives a fix to. */
export const STORAGE_FIX_RULES = ["MQ4009", "MQ4115", "MQ4118", "MQ4119", "MQ4120"] as const;

/** The text to copy out of an MQ4118 message: every "To …, add: <text>" it holds, one per line. */
export function copyTextOf(message: string): string | null {
  const parts = message.split(/ ?To [^,]+, add: /).slice(1);
  return parts.length ? parts.map((p) => p.trim()).join("\n") : null;
}

/**
 * The entity name, database and old code an MQ4119 message names. A text code is printed in quotes ('007' stays text), an integer code
 * without (7 stays a number).
 */
export function keptCodeOf(message: string): { entity: string; database: string; code: string | number } | null {
  const at = /type code of '([^']+)' in database '([^']+)' changed from (?:'(.*?)'|(-?[0-9]+)) to (?:'|-?[0-9])/.exec(message);
  if (!at) return null;
  return { entity: at[1], database: at[2], code: at[4] !== undefined ? Number(at[4]) : (at[3] ?? "") };
}

const KEYS_REFUSED = new Set(["foreignKeyEnd", "foreignKey", "promotedName", "shape", "attributes", "constants"]);

/** The relation's ends as documents write them. */
const endsOf = (relation: ModelJson | undefined): Json[] => ((relation as Json | undefined)?.ends as Json[] | undefined) ?? [];

/** Whether a relation has an end of several types, where a top-level `foreignKey` can move to an end's entry. */
export const hasSeveralTypes = (relation: ModelJson | undefined): boolean => endsOf(relation).some((e) => docEndHasSeveralTypes(e));

/** MQ4009 on a relation mapping: the refused key goes (its pointer is `/<key>`). */
export function mappingKeyFix(mapping: ModelJson, _relation: ModelJson | undefined, pointer: string): { json: ModelJson; label: string } | null {
  const key = /^\/([A-Za-z]+)$/.exec(pointer)?.[1];
  if (!key || !KEYS_REFUSED.has(key) || mapping.kind !== "mapping" || (mapping as Json)[key] === undefined) return null;
  const next = clone(mapping) as Json;
  delete next[key];
  return { json: next as ModelJson, label: "Remove the key" };
}

/** The key of a top-level `foreignKey` that can move to an end's entry: only on a relation with an end of several types. */
export function movableKey(mapping: ModelJson, relation: ModelJson | undefined, pointer: string): string | null {
  const key = (mapping as Json).foreignKey;
  return pointer === "/foreignKey" && mapping.kind === "mapping" && typeof key === "string" && hasSeveralTypes(relation) ? key : null;
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
    // The entity is found by id: the finding sits on the entity itself, or on the mapping that carries its code.
    const entity = element.kind === "entity" ? id : element.kind === "mapping" ? str((element as Json).entity) : undefined;
    return kept && entity ? { kind: "storage", action: "keep-code", entity, database: kept.database, code: kept.code, label: "Keep the stored code" } : null;
  }
  if (d.rule === "MQ4009") {
    const key = movableKey(element, relation, d.jsonPointer ?? "");
    const mappingRelation = str((element as Json).relation);
    if (key && mappingRelation)
      return { kind: "storage", action: "move-key", mapping: id, relation: mappingRelation, key, label: "Move it to the end's entry" };
    const fix = mappingKeyFix(element, relation, d.jsonPointer ?? "");
    return fix ? { kind: "update", id, json: fix.json, label: fix.label } : null;
  }
  return null;
}

/** The second fix of MQ4115 (open the Storage tab), and of MQ4009 on a key that can move (remove it instead). */
export function storageExtras(
  d: Diagnostic,
  element: ModelJson,
  relation?: ModelJson,
): (StorageFix | { kind: "update"; id: string; json: ModelJson; label: string })[] {
  const id = str(element.id);
  if (d.rule === "MQ4009" && id && movableKey(element, relation, d.jsonPointer ?? "")) {
    const fix = mappingKeyFix(element, relation, d.jsonPointer ?? "");
    return fix ? [{ kind: "update", id, json: fix.json, label: fix.label }] : [];
  }
  return d.rule === "MQ4115" && element.kind === "relation" && id ? [{ kind: "storage", action: "open", relation: id, label: "Open the Storage tab" }] : [];
}

/** The relation document an MQ4009 finding on a mapping needs: the one the mapping is for. */
export function relationOfMapping(d: Diagnostic, element: ModelJson | undefined): string | undefined {
  return d.rule === "MQ4009" && element?.kind === "mapping" ? str((element as Json).relation) : undefined;
}
