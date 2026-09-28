// Node positions kept per browser (package views, database diagrams; phase2-design.md 4.8).
import { local } from "./storage";

export type StoredPositions = Record<string, { x: number; y: number }>;

export function loadPositions(key: string): StoredPositions {
  return local.getJson<StoredPositions>(`mq.pos.${key}`) ?? {};
}

export function savePositions(key: string, positions: StoredPositions): void {
  local.setJson(`mq.pos.${key}`, positions);
}

/** The `dimensions` changes in a React Flow change list, as sizes by node id (null when none). */
export function measuredSizes(
  changes: readonly { type: string; id?: string; dimensions?: { width: number; height: number } }[],
): Record<string, { width: number; height: number }> | null {
  let sizes: Record<string, { width: number; height: number }> | null = null;
  for (const change of changes) {
    if (change.type !== "dimensions" || !change.id || !change.dimensions) continue;
    (sizes ??= {})[change.id] = change.dimensions;
  }
  return sizes;
}
