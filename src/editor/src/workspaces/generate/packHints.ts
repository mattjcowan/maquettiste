// Generation hints keyed by a pack name (`generation.<pack>` on an element and on its parts: enum members, table columns,
// category nodes, ...), as a pack rename sees them: the same walk as the engine's PackHints, pure.

/** Whether a document holds, at any depth, a `generation` map with the key `pack`. */
export function namesPack(node: unknown, pack: string): boolean {
  if (Array.isArray(node)) return node.some((item) => namesPack(item, pack));
  if (!node || typeof node !== "object") return false;
  for (const [key, value] of Object.entries(node)) {
    if (key === "generation" && value && typeof value === "object" && !Array.isArray(value) && Object.hasOwn(value, pack)) return true;
    if (namesPack(value, pack)) return true;
  }
  return false;
}

/**
 * Moves every `generation` hint keyed by `from` to `to`, in place and at the same place in its map; a map that already has
 * a `to` key is left as it is. Answers how many maps changed.
 */
export function renamePackHints(node: unknown, from: string, to: string): number {
  if (Array.isArray(node)) return node.reduce((n: number, item) => n + renamePackHints(item, from, to), 0);
  if (!node || typeof node !== "object") return 0;
  const record = node as Record<string, unknown>;
  let changed = 0;
  for (const [key, value] of Object.entries(record)) {
    const map = value as Record<string, unknown> | null;
    if (key === "generation" && map && typeof map === "object" && !Array.isArray(map) && Object.hasOwn(map, from) && !Object.hasOwn(map, to)) {
      record[key] = Object.fromEntries(Object.entries(map).map(([name, hint]) => [name === from ? to : name, hint]));
      changed++;
    } else changed += renamePackHints(value, from, to);
  }
  return changed;
}
