// What a database holds (engine-design.md D46, explorer-redesign.md 1.3): the entities its `byConvention` takes
// (all, the domains in `packages` with their sub-domains, or none) plus the entities a mapping element names, less the
// ones a mapping ignores. A file without `byConvention` keeps the rule from before 0.3.0: every entity when `packages`
// is empty, else the entities of `packages`. Free of React; the New database dialog, the "Map to database…" action,
// the database's Mapping section and the mock server share it.

export type ByConvention = "all" | "packages" | "none";

export interface DatabaseConvention {
  /** The effective setting. */
  mode: ByConvention;
  /** False for a file written before the member existed (the editor offers "Make explicit"). */
  explicit: boolean;
  /** The convention domains when the mode is `packages`. */
  packages: string[];
}

interface DatabaseLike {
  byConvention?: string | null;
  packages?: readonly string[] | null;
}

/** The effective convention of a database document. */
export function conventionOf(db: DatabaseLike): DatabaseConvention {
  const packages = [...(db.packages ?? [])];
  const mode = db.byConvention;
  if (mode === "all" || mode === "none") return { mode, explicit: true, packages: [] };
  if (mode === "packages") return { mode, explicit: true, packages };
  return packages.length ? { mode: "packages", explicit: false, packages } : { mode: "all", explicit: false, packages: [] };
}

/** Writes the convention on a database document: `byConvention` always, `packages` only for the `packages` mode. */
export function setConvention(json: Record<string, unknown>, mode: ByConvention, packages: readonly string[] = []): void {
  json.byConvention = mode;
  const list = mode === "packages" ? [...new Set(packages)].sort() : [];
  if (list.length) json.packages = list;
  else delete json.packages;
}

/** The convention member of a new database: none (the default), the picked domains, or all. */
export function newDatabaseConvention(choice: "none" | "pick" | "all", picked: readonly string[]): { byConvention: ByConvention; packages?: string[] } {
  if (choice === "all") return { byConvention: "all" };
  if (choice === "pick" && picked.length) return { byConvention: "packages", packages: [...new Set(picked)].sort() };
  return { byConvention: "none" };
}

/** Adds domains to a database's convention; false when every one is already taken (the mode is `all`, or listed). */
export function mapDomains(json: Record<string, unknown>, domains: readonly string[]): boolean {
  const current = conventionOf(json as DatabaseLike);
  if (current.mode === "all") {
    if (!current.explicit) json.byConvention = "all";
    return !current.explicit;
  }
  const next = [...new Set([...current.packages, ...domains])];
  if (current.explicit && next.length === current.packages.length) return false;
  setConvention(json, "packages", next);
  return true;
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

/** The phrase for the database's Mapping section. */
export function conventionLabel(convention: DatabaseConvention, nameOf: (id: string) => string): string {
  if (!convention.explicit && convention.mode === "all") return "By convention: all domains (unspecified)";
  if (convention.mode === "all") return "By convention: all domains";
  if (convention.mode === "none" || !convention.packages.length) return "By convention: nothing";
  return `By convention: ${convention.packages
    .map(nameOf)
    .sort((a, b) => a.localeCompare(b))
    .join(", ")}${convention.explicit ? "" : " (unspecified)"}`;
}

/** The hint the Databases explorer shows under a database that holds nothing. */
export const EMPTY_DATABASE_HINT = "Nothing is mapped here yet: map domains or entities from their menus or the Mapping section of this database's inspector";
