// The older way a database lays out tables (engine-design.md D46, explorer-redesign.md 1.3): the entities its
// `byConvention` takes (all, the domains in `packages` with their sub-domains, or none) plus the entities a mapping element
// names, less the ones a mapping ignores. A file without `byConvention` keeps the rule from before 0.3.0: every entity when
// `packages` is empty, else the entities of `packages`. New databases start with none (an entity is stored from its own
// Storage tab); Settings > Conventions shows what older projects still lay out and stops it. Free of React; that panel and
// the mock server share it.

import { conventionSchemas, entryOf, entryPackage, type ConventionEntry } from "./databaseSchemas";

export type ByConvention = "all" | "packages" | "none";

export interface DatabaseConvention {
  /** The effective setting. */
  mode: ByConvention;
  /** False for a file written before the member existed (the editor offers "Make explicit"). */
  explicit: boolean;
  /** The convention domains when the mode is `packages`. */
  packages: string[];
  /** Package id → the schema id its conventional tables go to (entries that name one; erratum E26). */
  schemas: Record<string, string>;
}

interface DatabaseLike {
  byConvention?: string | null;
  packages?: readonly ConventionEntry[] | null;
}

/** The effective convention of a database document. */
export function conventionOf(db: DatabaseLike): DatabaseConvention {
  const packages = (db.packages ?? []).map(entryPackage);
  const schemas = conventionSchemas(db as Record<string, unknown>);
  const mode = db.byConvention;
  if (mode === "all" || mode === "none") return { mode, explicit: true, packages: [], schemas: {} };
  if (mode === "packages") return { mode, explicit: true, packages, schemas };
  return packages.length ? { mode: "packages", explicit: false, packages, schemas } : { mode: "all", explicit: false, packages: [], schemas: {} };
}

/**
 * Writes the convention on a database document: `byConvention` always, `packages` only for the `packages` mode. Each
 * entry keeps its schema (from `schemas`, else the document's current entry); an entry without one is the package id.
 */
export function setConvention(
  json: Record<string, unknown>,
  mode: ByConvention,
  packages: readonly string[] = [],
  schemas: Readonly<Record<string, string | null | undefined>> = conventionSchemas(json),
): void {
  json.byConvention = mode;
  const list = mode === "packages" ? [...new Set(packages)].sort() : [];
  if (list.length) json.packages = list.map((p) => entryOf(p, schemas[p]));
  else delete json.packages;
}

/** Whether the convention takes the entities of a package (or of one of its ancestors); `parentOf` walks up. */
export function takesPackage(
  convention: DatabaseConvention,
  packageId: string | null | undefined,
  parentOf: (id: string) => string | null | undefined,
): boolean {
  if (convention.mode === "all") return true;
  if (convention.mode === "none") return false;
  const listed = new Set(convention.packages);
  const seen = new Set<string>();
  for (let id = packageId ?? null; id && !seen.has(id); id = parentOf(id) ?? null) {
    seen.add(id);
    if (listed.has(id)) return true;
  }
  return false;
}

/** Whether an entity lands in the database: a mapping names it (and does not ignore it), or the convention takes it. */
export function placesEntity(
  convention: DatabaseConvention,
  packageId: string | null | undefined,
  mapping: { ignore?: boolean | null } | null | undefined,
  parentOf: (id: string) => string | null | undefined,
): boolean {
  if (mapping) return mapping.ignore !== true;
  return takesPackage(convention, packageId, parentOf);
}

/** The phrase for a database's convention in Settings > Conventions. */
export function conventionLabel(convention: DatabaseConvention, nameOf: (id: string) => string): string {
  if (!convention.explicit && convention.mode === "all") return "By convention: all domains (unspecified)";
  if (convention.mode === "all") return "By convention: all domains";
  if (convention.mode === "none" || !convention.packages.length) return "By convention: nothing";
  return `By convention: ${convention.packages
    .map(nameOf)
    .sort((a, b) => a.localeCompare(b))
    .join(", ")}${convention.explicit ? "" : " (unspecified)"}`;
}

/** The hint the Databases explorer shows under a database that holds nothing yet. */
export const EMPTY_DATABASE_HINT = "Empty: New table…, New view… or New query… adds one here";
