// Writes of one table run one after another (`enqueueTableWrite`): the column grid, the inspector, the table editor's tabs and
// the explorer share one queue per table, so a second edit made before the first was saved reads the file the first left.
// `updateTable` is the one way to change a table outside a screen that holds it (the explorer's actions, the table editor and
// the inspector go through here too).
import type { QueryClient } from "@tanstack/react-query";
import { elementQuery, invalidateResolved } from "@/api/queries";
import type { TableView } from "@/api/types";
import { clone } from "@/lib/json";
import type { AppServices as Services } from "@/app/context";

type Json = Record<string, unknown>;

/** Per table (database and key): the queue of its writes. */
const queues = new Map<string, Promise<unknown>>();

/** Runs `write` after the table's earlier writes; resolves to what it resolves to. */
export function enqueueTableWrite<T>(database: string, key: string, write: () => Promise<T>): Promise<T> {
  const queueKey = `${database}|${key}`;
  const next = (queues.get(queueKey) ?? Promise.resolve()).catch(() => undefined).then(write);
  const settled = next.catch(() => undefined);
  queues.set(queueKey, settled);
  void settled.then(() => {
    if (queues.get(queueKey) === settled) queues.delete(queueKey);
  });
  return next;
}

/** The table `key` names in `tables`. */
export function shownTable(tables: readonly TableView[] | undefined, key: string | null | undefined): TableView | null {
  if (!tables || !key) return null;
  return tables.find((t) => t.key === key) ?? null;
}

/**
 * Changes one table file (any table of the database, by its key): one save, one undo step. `edit` may refuse (return false):
 * nothing is written. Runs after the table's earlier writes. Resolves to whether it was saved (false too when the save was
 * refused, or the file is in conflict with a newer version).
 */
export function updateTable(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  database: string,
  key: string,
  label: string,
  edit: (doc: Json) => void | boolean,
): Promise<boolean> {
  return enqueueTableWrite(database, key, async () => {
    const { store, drafts } = services;
    const base = await qc.fetchQuery({ ...elementQuery(key), staleTime: 0 });
    if (store.getState().drafts[key]?.status === "conflict") {
      store.getState().notify(`${label}: the table changed on disk; resolve the conflict first.`, "error");
      return false;
    }
    if (edit(clone((drafts.current(key) ?? base.json) as unknown as Json)) === false) return false;
    drafts.edit(key, (json) => void edit(json as unknown as Json), { base, label });
    const saved = await drafts.flushSaved(key);
    invalidateResolved(qc);
    if (!saved) {
      const draft = store.getState().drafts[key];
      const why =
        draft?.status === "conflict"
          ? "the table changed on disk; resolve the conflict first"
          : (draft?.diagnostics.find((d) => d.severity === "error")?.message ?? draft?.error ?? "the save was refused");
      store.getState().notify(`${label}: ${why}.`, "error");
    }
    return saved;
  });
}
