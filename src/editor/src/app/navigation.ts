// URL ↔ editor state. The URL carries the workspace, the diagram or database and `?sel=<id>`, so
// views are linkable (phase2-design.md 4.6).
import { useCallback } from "react";
import { useLocation, useNavigate } from "react-router";
import type { ElementSummary } from "@/api/types";
import { WORKSPACES, type Workspace } from "@/state/store";
import { useServices } from "./context";

export interface ParsedLocation {
  workspace: Workspace;
  diagram: string | null;
  database: string | null;
  settingsTab: string | null;
  selection: string[];
}

export function parseLocation(pathname: string, search: string): ParsedLocation {
  const [first, second] = pathname.split("/").filter(Boolean);
  const workspace = (WORKSPACES as string[]).includes(first ?? "") ? (first as Workspace) : "entities";
  const params = new URLSearchParams(search);
  const sel = params.get("sel");
  return {
    workspace,
    diagram: workspace === "entities" ? (second ?? null) : null,
    database: workspace === "database" ? (second ?? null) : null,
    settingsTab: workspace === "settings" ? (second ?? null) : null,
    selection: sel ? sel.split(",").filter(Boolean) : [],
  };
}

export function buildUrl(workspace: Workspace, segment: string | null, selection: string[], keep?: URLSearchParams): string {
  const params = new URLSearchParams(keep);
  params.delete("sel");
  if (selection.length) params.set("sel", selection.join(","));
  const query = params.toString();
  return `/${workspace}${segment ? `/${segment}` : ""}${query ? `?${query}` : ""}`;
}

/** Navigation that keeps the store and the URL in step. */
export function useEditorNavigation() {
  const navigate = useNavigate();
  const location = useLocation();
  const { store } = useServices();

  const segmentFor = useCallback(
    (workspace: Workspace) => {
      const s = store.getState();
      if (workspace === "entities") return s.activeDiagram;
      if (workspace === "database") return s.activeDatabase;
      if (workspace === "settings") return parseLocation(location.pathname, location.search).settingsTab;
      return null;
    },
    [store, location],
  );

  const keep = useCallback(() => {
    const params = new URLSearchParams(location.search);
    return params;
  }, [location.search]);

  const openWorkspace = useCallback(
    (workspace: Workspace) => {
      store.getState().setWorkspace(workspace);
      navigate(buildUrl(workspace, segmentFor(workspace), store.getState().selection, keep()));
    },
    [navigate, store, segmentFor, keep],
  );

  const openDiagram = useCallback(
    (id: string | null) => {
      store.getState().setActiveDiagram(id);
      store.getState().setWorkspace("entities");
      navigate(buildUrl("entities", id, store.getState().selection, keep()));
    },
    [navigate, store, keep],
  );

  const openDatabase = useCallback(
    (id: string | null) => {
      store.getState().setActiveDatabase(id);
      store.getState().setWorkspace("database");
      navigate(buildUrl("database", id, store.getState().selection, keep()));
    },
    [navigate, store, keep],
  );

  const openSettings = useCallback(
    (tab: string) => {
      store.getState().setWorkspace("settings");
      navigate(buildUrl("settings", tab, store.getState().selection, keep()));
    },
    [navigate, store, keep],
  );

  const select = useCallback(
    (ids: string[], pointer: string | null = null) => {
      const s = store.getState();
      s.select(ids, { pointer });
      navigate(buildUrl(s.workspace, segmentFor(s.workspace), ids, keep()), { replace: true });
    },
    [navigate, store, segmentFor, keep],
  );

  /** Go to an element: select it and open the workspace that shows its kind. */
  const reveal = useCallback(
    (summary: ElementSummary, pointer: string | null = null) => {
      const s = store.getState();
      if (summary.kind === "diagram") {
        openDiagram(summary.id);
        return;
      }
      if (summary.kind === "database") {
        s.select([summary.id], { pointer });
        openDatabase(summary.id);
        return;
      }
      if (["tag-vocabulary", "category-tree", "stereotype"].includes(summary.kind) && s.workspace !== "settings") {
        s.select([summary.id], { pointer });
        openSettings(summary.kind === "tag-vocabulary" ? "tags" : summary.kind === "category-tree" ? "categories" : "stereotypes");
        return;
      }
      select([summary.id], pointer);
    },
    [store, openDiagram, openDatabase, openSettings, select],
  );

  return { openWorkspace, openDiagram, openDatabase, openSettings, select, reveal };
}
