// The conventions draft (phase2-design.md 4.7). The draft remembers the settings version it was
// built on, so Save sends that hash; after a 409 only the convention keys the user changed are
// re-applied onto the disk version (never the whole stale document), and the user saves again.
import type { SettingsJson } from "@/api/types";
import { clone, jsonEqual } from "@/lib/json";

export interface ConventionsDraft {
  baseHash: string;
  baseJson: SettingsJson;
  json: SettingsJson;
}

type Bag = Record<string, unknown>;

function applyChanged(target: Bag, before: Bag | undefined, after: Bag | undefined): void {
  const keys = new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})]);
  for (const key of keys) {
    const was = before?.[key];
    const now = after?.[key];
    if (jsonEqual(was ?? null, now ?? null)) continue;
    if (now === undefined) delete target[key];
    else target[key] = clone(now);
  }
}

/** The disk document with the convention keys changed between `base` and `mine` applied onto it. */
export function rebaseConventions(base: SettingsJson, mine: SettingsJson, disk: SettingsJson): SettingsJson {
  const result = clone(disk) as SettingsJson & { conventions?: Bag; databases?: Record<string, Bag> };
  const b = base as { conventions?: Bag; databases?: Record<string, Bag> };
  const m = mine as { conventions?: Bag; databases?: Record<string, Bag> };
  const conventions = (result.conventions ?? {}) as Bag;
  applyChanged(conventions, b.conventions, m.conventions);
  if (Object.keys(conventions).length) result.conventions = conventions as never;
  else delete result.conventions;
  const databases = (result.databases ?? {}) as Record<string, Bag>;
  for (const name of new Set([...Object.keys(b.databases ?? {}), ...Object.keys(m.databases ?? {})])) {
    const target = (databases[name] ??= {});
    applyChanged(target, b.databases?.[name], m.databases?.[name]);
    if (Object.keys(target).length === 0) delete databases[name];
  }
  if (Object.keys(databases).length) result.databases = databases as never;
  else delete result.databases;
  return result;
}

/** Kept outside the component so leaving the Settings workspace does not drop unsaved conventions. */
let kept: ConventionsDraft | null = null;
export const keptConventionsDraft = {
  get: (): ConventionsDraft | null => kept,
  set: (draft: ConventionsDraft | null): void => {
    kept = draft;
  },
};
