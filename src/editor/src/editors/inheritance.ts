// What the entity editor's Inheritance tab and Virtual rows show (explorer-redesign.md 3.6), as pure functions over
// documents the editor already reads: the strategy of a hierarchy per database, read the way the resolver reads it
// (the hierarchy root's mapping, else the database's conventions, else the project's, else tph), and the attributes
// an entity's stereotypes contribute (SPEC 6).
import type { AttributeDoc } from "@/api/types";

export type InheritanceStrategy = "tph" | "tpt" | "tpc";

export const STRATEGY_LABELS: Record<InheritanceStrategy, string> = {
  tph: "One table for the hierarchy (tph)",
  tpt: "One table per entity, joined (tpt)",
  tpc: "One table per concrete entity (tpc)",
};

/** Where a strategy comes from, most specific first. */
export type StrategySource = "mapping" | "database" | "project" | "default";

export const SOURCE_LABELS: Record<StrategySource, string> = {
  mapping: "the root's mapping",
  database: "the database's conventions",
  project: "the project's conventions",
  default: "the default",
};

export interface InheritanceRow {
  database: string;
  databaseName: string;
  strategy: InheritanceStrategy;
  source: StrategySource;
  /** This entity's discriminator value in that database, when its mapping sets one. */
  discriminatorValue?: string | number;
}

interface MappingLike {
  database?: string | null;
  inheritance?: InheritanceStrategy | null;
  discriminatorValue?: string | number | null;
}

interface ConventionsLike {
  inheritance?: InheritanceStrategy | null;
}

/** Whether the entity is in a hierarchy: it has a base entity or an entity derives from it. */
export function inHierarchy(base: string | null | undefined, derivedCount: number): boolean {
  return !!base || derivedCount > 0;
}

/**
 * One row per database: the hierarchy's strategy and where it comes from, and the entity's discriminator value.
 * `perDatabase` is the settings' `databases` block, keyed by database name or id.
 */
export function inheritanceRows(input: {
  databases: readonly { id: string; name: string }[];
  rootMappings: readonly MappingLike[];
  ownMappings: readonly MappingLike[];
  conventions?: ConventionsLike | null;
  perDatabase?: Readonly<Record<string, ConventionsLike | undefined>> | null;
}): InheritanceRow[] {
  return input.databases.map((db) => {
    const root = input.rootMappings.find((m) => m.database === db.id)?.inheritance;
    const database = (input.perDatabase?.[db.name] ?? input.perDatabase?.[db.id])?.inheritance;
    const project = input.conventions?.inheritance;
    const [strategy, source]: [InheritanceStrategy, StrategySource] = root
      ? [root, "mapping"]
      : database
        ? [database, "database"]
        : project
          ? [project, "project"]
          : ["tph", "default"];
    const own = input.ownMappings.find((m) => m.database === db.id)?.discriminatorValue;
    return { database: db.id, databaseName: db.name, strategy, source, ...(own != null ? { discriminatorValue: own } : {}) };
  });
}

export interface VirtualAttribute {
  stereotype: string;
  attribute: AttributeDoc;
}

/** The attributes the entity's stereotypes add, in the order the entity lists its stereotypes; unknown keys add none. */
export function virtualAttributes(
  keys: readonly string[] | undefined,
  stereotypes: readonly { key: string; attributes?: readonly AttributeDoc[] }[],
): VirtualAttribute[] {
  const byKey = new Map(stereotypes.map((s) => [s.key, s]));
  return (keys ?? []).flatMap((key) => (byKey.get(key)?.attributes ?? []).map((attribute) => ({ stereotype: key, attribute })));
}

export interface FieldSource {
  /** The entity whose level contributes the attribute: a base entity or the entity itself. */
  from: string;
  /** The stereotype that adds it at that level, if any (a virtual attribute). */
  stereotype?: string;
  attribute: AttributeDoc;
}

/**
 * The attributes an entity gets from each level of its hierarchy, the way the resolver flattens them (SPEC 6): root
 * first, each level's own attributes then the attributes its stereotypes add, the first occurrence of an attribute id
 * wins (a stereotype applied at two levels, or two stereotypes sharing an attribute, contribute it once). `levels` is
 * root first and ends with the entity itself.
 */
export function fieldSources(
  levels: readonly { id: string; attributes?: readonly AttributeDoc[]; stereotypes?: readonly string[] }[],
  stereotypes: readonly { key: string; attributes?: readonly AttributeDoc[] }[],
): FieldSource[] {
  const seen = new Set<string>();
  const out: FieldSource[] = [];
  const add = (source: FieldSource) => {
    if (seen.has(source.attribute.id)) return;
    seen.add(source.attribute.id);
    out.push(source);
  };
  for (const level of levels) {
    for (const attribute of level.attributes ?? []) add({ from: level.id, attribute });
    for (const v of virtualAttributes(level.stereotypes, stereotypes)) add({ from: level.id, stereotype: v.stereotype, attribute: v.attribute });
  }
  return out;
}

// ------------------------------------------------------------------ the relationship editor's Mappings tab

export type RelationShape = "foreign-key" | "junction" | "promoted";

export interface RelationMappingLike {
  id: string;
  database?: string | null;
  relation?: string | null;
  shape?: RelationShape | null;
  foreignKeyEnd?: string | null;
  promotedName?: string | null;
  ignore?: boolean | null;
}

export interface RelationMappingRow {
  database: string;
  databaseName: string;
  /** The customised mapping of the relation in that database, if any. */
  mapping: RelationMappingLike | null;
  /** The shape the mapping asks for; null when it follows the conventions. */
  shape: RelationShape | null;
  ignored: boolean;
  /** The resolved tables of that database that stand for the relation (a junction table), when known. */
  tables: { key: string; name: string }[];
}

/** One row per database: the relation's customised mapping there, and the tables the resolver made for it. */
export function relationMappingRows(
  relation: string,
  databases: readonly { id: string; name: string }[],
  mappings: readonly RelationMappingLike[],
  tablesOf: (database: string) => readonly { key: string; name: string; relationId: string | null }[] | undefined,
): RelationMappingRow[] {
  return databases.map((db) => {
    const mapping = mappings.find((m) => m.relation === relation && m.database === db.id) ?? null;
    const tables = (tablesOf(db.id) ?? []).filter((t) => t.relationId === relation).map((t) => ({ key: t.key, name: t.name }));
    return { database: db.id, databaseName: db.name, mapping, shape: mapping?.shape ?? null, ignored: mapping?.ignore === true, tables };
  });
}

// ------------------------------------------------------------------ editing (the Inheritance and Mappings tabs)

/**
 * True when making `candidate` the base of `id` would close a loop: the candidate is the entity itself or derives
 * from it (walking the candidate's base chain reaches `id`). A chain that already loops elsewhere stops after 256.
 */
export function wouldCycle(baseOf: (id: string) => string | null | undefined, id: string, candidate: string): boolean {
  for (let c: string | null | undefined = candidate, guard = 0; c && guard < 256; c = baseOf(c), guard++) if (c === id) return true;
  return false;
}

/** The entities that can be the base of `id` (none of them derives from it), sorted by name. */
export function baseChoices<T extends { id: string; name: string; base?: string | null }>(entities: readonly T[], id: string): T[] {
  const byId = new Map(entities.map((e) => [e.id, e]));
  return entities.filter((e) => !wouldCycle((x) => byId.get(x)?.base, id, e.id)).sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
}

/** Sets or clears (null: follow the conventions) a mapping's inheritance strategy. */
export function setInheritance(json: Record<string, unknown>, strategy: InheritanceStrategy | null): void {
  if (strategy) json.inheritance = strategy;
  else delete json.inheritance;
}

/** Sets or clears a mapping's discriminator value; a numeric text stays text (the column type decides). */
export function setDiscriminator(json: Record<string, unknown>, value: string): void {
  if (value.trim()) json.discriminatorValue = value.trim();
  else delete json.discriminatorValue;
}

/**
 * A relation mapping's shape and what goes with it: the junction table (a designed table's id) only with the
 * junction shape, the promoted entity's name only with the promoted shape; null shape follows the conventions.
 */
export function setRelationShape(
  json: Record<string, unknown>,
  change: { shape?: RelationShape | null; junctionTable?: string | null; promotedName?: string | null },
): void {
  if (change.shape !== undefined) {
    if (change.shape) json.shape = change.shape;
    else delete json.shape;
  }
  const shape = json.shape as RelationShape | undefined;
  if (change.junctionTable !== undefined) {
    if (change.junctionTable) json.junctionTable = change.junctionTable;
    else delete json.junctionTable;
  }
  if (change.promotedName !== undefined) {
    if (change.promotedName?.trim()) json.promotedName = change.promotedName.trim();
    else delete json.promotedName;
  }
  if (shape !== "junction") delete json.junctionTable;
  if (shape !== "promoted") delete json.promotedName;
}
