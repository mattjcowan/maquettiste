// The snapshot UI's state (docs/engineering/snapshots.md section 10): which dialog is open, the compare view's sides, and
// the switch between the working model and a snapshot shown "as of". The scope itself lives in api/snapshotScope.ts (the
// HTTP client and the query client read it); the URL's `?snapshot=` is its source, so a reload or a link keeps it.
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { useLocation, useNavigate } from "react-router";
import { createStore } from "zustand/vanilla";
import { useStore } from "zustand";
import type { QueryClient } from "@tanstack/react-query";
import type { Role, SnapshotInfo, SnapshotRestoreResult } from "@/api/types";
import { dropSnapshotLoaders, keys, useSession } from "@/api/queries";
import { scopeOfHash, setSnapshotScope, snapshotFromSearch, snapshotScope, subscribeSnapshotScope, withSnapshotParam } from "@/api/snapshotScope";
import { useServices } from "@/app/context";

export const WORKING = "working";

export type SnapshotDialog =
  | { kind: "take" }
  | { kind: "edit"; snapshot: SnapshotInfo }
  | { kind: "delete"; snapshot: SnapshotInfo }
  | { kind: "restore"; snapshot: SnapshotInfo }
  | { kind: "restored"; result: SnapshotRestoreResult }
  | { kind: "compare-with"; snapshot: SnapshotInfo };

export interface SnapshotUiState {
  dialog: SnapshotDialog | null;
  /** The compare view's sides (snapshot ids or `working`), or null when it is closed. */
  compare: { from: string; to: string } | null;
}

export const snapshotUi = createStore<SnapshotUiState>()(() => ({ dialog: null, compare: null }));

export function useSnapshotUi<T>(select: (s: SnapshotUiState) => T): T {
  return useStore(snapshotUi, select);
}

export const openSnapshotDialog = (dialog: SnapshotDialog | null): void => snapshotUi.setState({ dialog });
export const openCompare = (from: string, to: string): void => snapshotUi.setState({ compare: { from, to }, dialog: null });
export const closeCompare = (): void => snapshotUi.setState({ compare: null });

// ---------------------------------------------------------------- roles

const RANK: Record<Role, number> = { viewer: 0, editor: 1, maintainer: 2, admin: 3 };

/** What each snapshot action needs (the API's x-maquettiste-role). */
export const SNAPSHOT_ROLES = {
  open: "viewer",
  compare: "viewer",
  take: "editor",
  edit: "editor",
  publish: "editor",
  export: "editor",
  restore: "maintainer",
  delete: "maintainer",
  import: "maintainer",
} as const satisfies Record<string, Role>;

export type SnapshotAction = keyof typeof SNAPSHOT_ROLES;

/** True when `role` may do `action`; an unknown role (the session not read yet) may only look. */
export function allows(role: Role | null | undefined, action: SnapshotAction): boolean {
  const need = SNAPSHOT_ROLES[action];
  return RANK[role ?? "viewer"] >= RANK[need];
}

/** Why an action is disabled for this role, for its tooltip; null when it is allowed. */
export function deniedReason(role: Role | null | undefined, action: SnapshotAction): string | null {
  return allows(role, action) ? null : `Needs the ${SNAPSHOT_ROLES[action]} role (you are ${role ?? "a viewer"}).`;
}

export function useRole(): Role | null {
  return useSession().data?.user.role ?? null;
}

// ---------------------------------------------------------------- as of

/** The snapshot shown, or null for the working model; re-renders on every switch. */
export function useSnapshotScope(): string | null {
  return useSyncExternalStore(subscribeSnapshotScope, snapshotScope, () => null);
}

/**
 * Switches the scope the whole page reads. Leaving a snapshot drops its cached queries and index loader and marks every
 * working-model query stale (realtime events are not applied while a snapshot is shown), so the views read the working
 * model again, the index with its ETag.
 */
export function applySnapshotScope(qc: QueryClient, next: string | null): void {
  const previous = snapshotScope();
  if (previous === next) return;
  setSnapshotScope(next);
  if (previous) {
    qc.removeQueries({ predicate: (q) => scopeOfHash(q.queryHash) === previous });
    dropSnapshotLoaders(next);
  }
  if (!next) void qc.invalidateQueries({ predicate: (q) => scopeOfHash(q.queryHash) === null && q.queryKey[0] !== keys.snapshots[0] });
}

/** URL → scope: `?snapshot=` decides (the back button, a pasted link). */
export function useSnapshotUrlSync(): void {
  const location = useLocation();
  const { queryClient } = useServices();
  const fromUrl = snapshotFromSearch(location.search);
  useEffect(() => applySnapshotScope(queryClient, fromUrl), [fromUrl, queryClient]);
}

/** Opening a snapshot as of and going back: the URL changes, and useSnapshotUrlSync follows it. */
export function useAsOfNavigation() {
  const navigate = useNavigate();
  const location = useLocation();
  const { drafts, store } = useServices();

  const open = useCallback(
    async (id: string): Promise<boolean> => {
      // Edits still being saved go out first: nothing is saved while a snapshot is shown.
      await drafts.flushAll();
      if (drafts.hasUnsaved()) {
        store.getState().notify("Some edits are not saved (invalid or in conflict). Fix or discard them before opening a snapshot.", "error");
        return false;
      }
      closeCompare();
      navigate(`${location.pathname}${withSnapshotParam(location.search, id)}`);
      return true;
    },
    [drafts, store, navigate, location.pathname, location.search],
  );

  const back = useCallback(() => {
    navigate(`${location.pathname}${withSnapshotParam(location.search, null)}`);
  }, [navigate, location.pathname, location.search]);

  return { open, back };
}

/** "2026-10-05 14:30" in the viewer's time zone, from the snapshot's UTC time. */
export function snapshotTime(createdUtc: string): string {
  const date = new Date(createdUtc);
  if (Number.isNaN(date.getTime())) return createdUtc;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** A side's label: "Working model", or the snapshot's name. */
export function sideLabel(side: string, list: readonly SnapshotInfo[] | undefined): string {
  if (side === WORKING) return "Working model";
  return list?.find((s) => s.id === side)?.name ?? side;
}
