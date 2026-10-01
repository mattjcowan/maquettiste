// The explorer's pending delete (explorer-redesign.md 1.8, engine-design.md 15.1): the remove action records the
// ids it was asked to delete, and the delete dialog, mounted with the explorer, reads both delete plans for them
// and offers the two ways through. A tiny external store, so the action (a hook callback) and the dialog (a
// component) share it without a provider.
import { useSyncExternalStore } from "react";

export interface DeleteRequest {
  ids: string[];
  names: ReadonlyMap<string, string>;
}

let current: DeleteRequest | null = null;
const listeners = new Set<() => void>();

export function requestDelete(request: DeleteRequest | null): void {
  current = request;
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useDeleteRequest(): DeleteRequest | null {
  return useSyncExternalStore(subscribe, () => current);
}
