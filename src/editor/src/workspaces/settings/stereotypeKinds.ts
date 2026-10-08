// The kinds a stereotype can apply to (the stereotype schema's `appliesTo` items), grouped as the form shows them.
import type { StereotypeDoc } from "@/api/types";

export type StereotypeKind = NonNullable<StereotypeDoc["appliesTo"]>[number];

export const STEREOTYPE_KIND_GROUPS = [
  { label: "Domain model", kinds: ["package", "entity", "value-object", "scalar-type", "enum", "relation", "reference-type", "seed", "diagram"] },
  { label: "Parts", kinds: ["attribute", "enum-member"] },
  { label: "Databases", kinds: ["database", "table", "column", "view", "sequence", "routine", "database-type", "sql-object", "query", "mapping"] },
  { label: "Processes", kinds: ["process", "actor", "scenario", "state", "transition", "event"] },
  { label: "Settings", kinds: ["tag-vocabulary", "category-tree", "stereotype"] },
] as const satisfies readonly { label: string; kinds: readonly StereotypeKind[] }[];

/** Every kind, in the groups' order. */
export const STEREOTYPE_KINDS: readonly StereotypeKind[] = STEREOTYPE_KIND_GROUPS.flatMap((g) => g.kinds);

/** Fails to compile when the schema gains a kind the groups leave out. */
export type StereotypeKindsCovered = [Exclude<StereotypeKind, (typeof STEREOTYPE_KIND_GROUPS)[number]["kinds"][number]>] extends [never] ? true : never;
export const STEREOTYPE_KINDS_COVERED: StereotypeKindsCovered = true;
