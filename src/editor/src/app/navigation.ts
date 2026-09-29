// URL ↔ editor state. The URL carries the workspace, the diagram or database and `?sel=<id>`, so
// views are linkable (phase2-design.md 4.6).
import { useCallback } from "react";
import { useLocation, useNavigate } from "react-router";
import type { ElementSummary } from "@/api/types";
import { WORKSPACES, type EditorStore, type Workspace } from "@/state/store";
import { keys } from "@/api/queries";
import { placeOf } from "@/search/engine";
import { useServices } from "./context";
import { activate, hasEditor, openTab, setView } from "@/editors/tabs";

/** The centre area shows the screen again, in front of the editor tabs. */
const showScreen = (store: EditorStore) => store.getState().updateEditors((e) => activate(e, null));

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
  const { store, queryClient } = useServices();

  const segmentFor = useCallback(
    (workspace: Workspace) => {
      const s = store.getState();
      if (workspace === "entities") return s.activeDiagram;
      if (workspace === "database") return s.activeDatabase;
      if (workspace === "settings") return parseLocation(location.pathname, location.search).settingsTab ?? s.settingsTab;
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
      showScreen(store);
      store.getState().setWorkspace(workspace);
      navigate(buildUrl(workspace, segmentFor(workspace), store.getState().selection, keep()));
    },
    [navigate, store, segmentFor, keep],
  );

  const openDiagram = useCallback(
    (id: string | null, options?: { keepEditors?: boolean }) => {
      // The Domain model screen's default pick after load keeps the editors: an editor the user opened in the first
      // second (a double-click in the tree) must not be hidden by it. The user's own choice brings the screen forward.
      if (!options?.keepEditors) showScreen(store);
      store.getState().setActiveDiagram(id);
      store.getState().setWorkspace("entities");
      navigate(buildUrl("entities", id, store.getState().selection, keep()));
    },
    [navigate, store, keep],
  );

  const openDatabase = useCallback(
    (id: string | null) => {
      showScreen(store);
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
      // Reference types, and the seeds of reference types, live only in the Reference data screen (RT 1.9).
      const target =
        summary.kind === "seed" && summary.target ? queryClient.getQueryData<ElementSummary[]>(keys.index)?.find((r) => r.id === summary.target) : undefined;
      if (summary.kind === "reference-type" || target?.kind === "reference-type") {
        s.select([summary.id], { pointer });
        if (s.workspace !== "reference-data") openWorkspace("reference-data");
        else select([summary.id], pointer);
        return;
      }
      // The seed of an entity or a relation is edited on that element's Seed data tab (its Rows grid).
      if (summary.kind === "seed" && (target?.kind === "entity" || target?.kind === "relation")) {
        const kind = target.kind;
        if (s.workspace === "settings" || s.workspace === "reference-data" || s.workspace === "generate") {
          s.select([target.id], { pointer: null });
          openWorkspace("entities");
        } else select([target.id], null);
        s.updateEditors((e) => openTab(setView(e, kind, "seed-data"), { id: target.id, kind }, { pin: true }));
        return;
      }
      // A domain's own vocabulary lives on the domain editor's Tags or Categories tab (1.11).
      if ((summary.kind === "tag-vocabulary" || summary.kind === "category-tree") && summary.package) {
        const domain = queryClient.getQueryData<ElementSummary[]>(keys.index)?.find((r) => r.id === summary.package);
        if (domain) {
          select([domain.id], pointer);
          s.updateEditors((e) =>
            openTab(setView(e, "package", summary.kind === "tag-vocabulary" ? "tags" : "categories"), { id: domain.id, kind: "package" }, { pin: true }),
          );
          return;
        }
      }
      if (["tag-vocabulary", "category-tree", "stereotype"].includes(summary.kind) && s.workspace !== "settings") {
        s.select([summary.id], { pointer });
        openSettings(summary.kind === "tag-vocabulary" ? "tags" : summary.kind === "category-tree" ? "categories" : "stereotypes");
        return;
      }
      // Settings, Reference data and Generate have no element inspector: going to an element shows its screen.
      if (s.workspace === "settings" || s.workspace === "reference-data" || s.workspace === "generate") {
        if (!["tag-vocabulary", "category-tree", "stereotype"].includes(summary.kind)) {
          s.select([summary.id], { pointer });
          openWorkspace("entities");
          return;
        }
      }
      select([summary.id], pointer);
    },
    [store, queryClient, openDiagram, openDatabase, openSettings, openWorkspace, select],
  );

  const summaryOf = useCallback((id: string) => queryClient.getQueryData<ElementSummary[]>(keys.index)?.find((r) => r.id === id), [queryClient]);

  /** Shows the explorer that lists a kind, unless it already shows (active or pinned beside). */
  const showPlace = useCallback(
    (place: ReturnType<typeof placeOf>) => {
      const e = store.getState().explorer;
      if (place !== "settings" && e.active !== place && e.pinned !== place) store.getState().setSidebar(place);
    },
    [store],
  );

  /**
   * Go to definition, a where-used row or a breadcrumb (explorer-redesign.md 3.3): shows the explorer that lists the
   * element, selects and reveals it, and asks the canvas to centre it when it is a card there. False when the element
   * is not in the index.
   */
  const goTo = useCallback(
    (id: string, pointer: string | null = null): boolean => {
      const summary = summaryOf(id);
      if (!summary) {
        store.getState().notify("That element is not in the model.", "error");
        return false;
      }
      showPlace(placeOf(summary.kind));
      reveal(summary, pointer);
      store.getState().requestCenter(id);
      return true;
    },
    [summaryOf, store, reveal, showPlace],
  );

  /** Back or forward through the selection history (Alt+Left, Alt+Right). */
  const travel = useCallback(
    (direction: "back" | "forward"): boolean => {
      const s = store.getState();
      const ids = s.travel(direction);
      if (!ids) return false;
      const summary = summaryOf(ids[0]);
      if (summary) showPlace(placeOf(summary.kind));
      navigate(buildUrl(s.workspace, segmentFor(s.workspace), ids, keep()), { replace: true });
      s.requestCenter(ids[0]);
      return true;
    },
    [store, summaryOf, navigate, segmentFor, keep, showPlace],
  );

  /** Opens an element in its editor tab (3.6): a preview tab unless `pin`; other kinds are revealed as before. */
  const openEditor = useCallback(
    (summary: ElementSummary, pin: boolean) => {
      reveal(summary);
      const kind = summary.kind;
      if (hasEditor(kind)) store.getState().updateEditors((e) => openTab(e, { id: summary.id, kind }, { pin }));
    },
    [reveal, store],
  );

  /** Edit seed data: the entity's or relation's editor, pinned, on its Seed data tab. */
  const openSeedData = useCallback(
    (summary: ElementSummary) => {
      if (summary.kind !== "entity" && summary.kind !== "relation") return;
      const kind = summary.kind;
      reveal(summary);
      store.getState().updateEditors((e) => openTab(setView(e, kind, "seed-data"), { id: summary.id, kind }, { pin: true }));
    },
    [reveal, store],
  );

  return { openWorkspace, openDiagram, openDatabase, openSettings, select, reveal, goTo, travel, openEditor, openSeedData };
}
