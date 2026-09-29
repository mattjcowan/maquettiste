// The editor shell (SPEC Section 14, phase2-design.md 4.8): top bar, workspace rail, explorer,
// the active workspace, the inspector and the bottom panel, with resizable, collapsible regions
// in the F6 focus order rail → explorer → center → inspector → bottom.
import { Component, lazy, Suspense, useEffect, useRef, type ComponentType, type ReactNode } from "react";
import { QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter, useLocation } from "react-router";
import { Explorer } from "@/explorer/Explorer";
import { Inspector } from "@/inspector/Inspector";
import { EditorArea, EditorTabBar } from "@/editors/EditorTabs";
import { CommandPalette, QuickOpen } from "@/palette/CommandPalette";
import { NewElementHost } from "@/explorer/NewElementDialog";
import { ConflictDialog } from "@/inspector/ConflictDialog";
import { Splitter } from "@/components/ui/splitter";
import { Spinner } from "@/components/ui/misc";
import { TooltipProvider } from "@/components/ui/tooltip";
import { applyTheme, followSystemTheme } from "@/design/theme";
import { saveLayout, useEditor, type EditorState, type Workspace } from "@/state/store";
import { Banners, Notices } from "./Banners";
import { BottomPanel } from "./BottomPanel";
import { Breadcrumbs } from "./Breadcrumbs";
import { ServicesProvider, useServices, type AppServices } from "./context";
import { parseLocation } from "./navigation";
import { Rail } from "./Rail";
import { GenerateExplorer } from "@/workspaces/generate/GenerateExplorer";
import { useGlobalShortcuts } from "./shortcuts";
import { TopBar } from "./TopBar";
import { SCREEN_LABELS } from "@/model/labels";

function named<K extends string>(load: () => Promise<Record<K, ComponentType>>, name: K) {
  return lazy(() => load().then((m) => ({ default: m[name] })));
}

const WORKSPACE_VIEWS: Record<Workspace, ComponentType> = {
  entities: named(() => import("@/workspaces/entities/EntitiesWorkspace"), "EntitiesWorkspace"),
  "reference-data": named(() => import("@/workspaces/reference-data/ReferenceDataWorkspace"), "ReferenceDataWorkspace"),
  database: named(() => import("@/workspaces/database/DatabaseWorkspace"), "DatabaseWorkspace"),
  mappings: named(() => import("@/workspaces/mappings/MappingsWorkspace"), "MappingsWorkspace"),
  generate: named(() => import("@/workspaces/generate/GenerateWorkspace"), "GenerateWorkspace"),
  settings: named(() => import("@/workspaces/settings/SettingsWorkspace"), "SettingsWorkspace"),
};

export const LIMITS = {
  explorer: { min: 240, max: 480 },
  inspector: { min: 320, max: 560 },
  bottom: { min: 120, max: 600 },
} as const;

export function App({ services, basename }: { services: AppServices; basename?: string }) {
  return (
    <QueryClientProvider client={services.queryClient}>
      <ServicesProvider services={services}>
        <TooltipProvider delayDuration={400}>
          <BrowserRouter basename={basename}>
            <Shell />
          </BrowserRouter>
        </TooltipProvider>
      </ServicesProvider>
    </QueryClientProvider>
  );
}

/** Keeps one failing region from blanking the whole shell; the error shows in its place. */
export class RegionBoundary extends Component<{ name: string; children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) {
    return { error };
  }
  componentDidUpdate(previous: { name: string }) {
    if (previous.name !== this.props.name && this.state.error) this.setState({ error: null });
  }
  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div role="alert" className="m-4 rounded-[8px] border border-default bg-surface p-2 text-[13px]">
        <p className="font-semibold text-danger">The {this.props.name} view failed.</p>
        <pre className="mt-2 whitespace-pre-wrap font-mono text-[12px] text-secondary">{this.state.error.message}</pre>
      </div>
    );
  }
}

/** URL → store: the workspace, the diagram or database and `?sel=` follow the address bar. */
function useLocationSync(): void {
  const { store } = useServices();
  const location = useLocation();
  useEffect(() => {
    const parsed = parseLocation(location.pathname, location.search);
    const s = store.getState();
    if (s.workspace !== parsed.workspace) s.setWorkspace(parsed.workspace);
    if (parsed.diagram !== null && parsed.diagram !== s.activeDiagram) s.setActiveDiagram(parsed.diagram);
    if (parsed.database !== null && parsed.database !== s.activeDatabase) s.setActiveDatabase(parsed.database);
    if (parsed.selection.join(",") !== s.selection.join(",")) s.select(parsed.selection);
  }, [location.pathname, location.search, store]);
}

/** Store → <html data-theme>, following the OS while the choice is "system". */
function useThemeSync(): void {
  const { store } = useServices();
  const theme = useEditor(store, (s) => s.theme);
  useEffect(() => applyTheme(theme), [theme]);
  useEffect(() => followSystemTheme(() => store.getState().theme), [store]);
}

/** Persists panel sizes (mq.layout) a moment after the last resize. */
function useLayoutPersistence(): void {
  const { store } = useServices();
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () =>
      store.subscribe((state, previous) => {
        if (state.explorerSize === previous.explorerSize && state.inspectorSize === previous.inspectorSize && state.bottomSize === previous.bottomSize) return;
        if (timer.current) clearTimeout(timer.current);
        timer.current = setTimeout(() => saveLayout(store.getState()), 300);
      }),
    [store],
  );
}

function Shell() {
  const { store } = useServices();
  useLocationSync();
  useThemeSync();
  useLayoutPersistence();
  useGlobalShortcuts();

  const workspace = useEditor(store, (s) => s.workspace);
  const sidebar = useEditor(store, (s) => s.explorer.active);
  const pinned = useEditor(store, (s) => s.explorer.pinned);
  const second = sidebar !== "generate" && pinned && pinned !== sidebar ? pinned : null;
  const explorerSize = useEditor(store, (s) => s.explorerSize);
  const inspectorSize = useEditor(store, (s) => s.inspectorSize);
  const bottomSize = useEditor(store, (s) => s.bottomSize);
  const explorerCollapsed = useEditor(store, (s) => s.explorerCollapsed);
  const inspectorCollapsed = useEditor(store, (s) => s.inspectorCollapsed);
  const bottomCollapsed = useEditor(store, (s) => s.bottomCollapsed);
  const resize = (patch: Partial<EditorState>) => store.setState(patch);
  const View = WORKSPACE_VIEWS[workspace];

  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-app text-primary" data-testid="shell">
      <Banners />
      <TopBar />
      <div className="flex min-h-0 flex-1">
        <Rail />
        {!explorerCollapsed && (
          <>
            <aside
              id="mq-explorer"
              aria-label="Explorer"
              data-region="explorer"
              className="flex min-h-0 shrink-0 flex-col border-r border-default bg-surface"
              style={{ width: second ? explorerSize * 2 : explorerSize }}
            >
              {sidebar === "generate" ? (
                <GenerateExplorer />
              ) : (
                <div className="flex h-full min-h-0">
                  <Explorer key={sidebar} id={sidebar} />
                  {second ? (
                    <>
                      <div className="w-px shrink-0 bg-default" aria-hidden />
                      <Explorer key={`pinned-${second}`} id={second} pinned />
                    </>
                  ) : null}
                </div>
              )}
            </aside>
            <Splitter
              orientation="vertical"
              value={explorerSize}
              {...LIMITS.explorer}
              direction={1}
              label="Resize the explorer"
              controls="mq-explorer"
              onChange={(v) => resize({ explorerSize: v })}
            />
          </>
        )}
        <div className="flex min-w-0 flex-1 flex-col">
          {workspace === "generate" || workspace === "settings" ? null : <Breadcrumbs />}
          <EditorTabBar workspace={workspace} />
          <main
            aria-label={`${SCREEN_LABELS[workspace]} screen`}
            data-region="center"
            className="relative min-h-0 flex-1 overflow-hidden bg-canvas"
            data-testid={`workspace-${workspace}`}
          >
            <Suspense
              fallback={
                <div className="flex h-full items-center justify-center">
                  <Spinner />
                </div>
              }
            >
              <RegionBoundary name={workspace}>
                <View />
              </RegionBoundary>
              <RegionBoundary name="editor">
                <EditorArea />
              </RegionBoundary>
            </Suspense>
          </main>
          {!bottomCollapsed && (
            <Splitter
              orientation="horizontal"
              value={bottomSize}
              {...LIMITS.bottom}
              direction={-1}
              label="Resize the bottom panel"
              controls="mq-bottom"
              onChange={(v) => resize({ bottomSize: v })}
            />
          )}
          <div id="mq-bottom" data-region="bottom" className="shrink-0 border-t border-default" style={bottomCollapsed ? undefined : { height: bottomSize }}>
            <BottomPanel />
          </div>
        </div>
        {!inspectorCollapsed && (
          <>
            <Splitter
              orientation="vertical"
              value={inspectorSize}
              {...LIMITS.inspector}
              direction={-1}
              label="Resize the inspector"
              controls="mq-inspector"
              onChange={(v) => resize({ inspectorSize: v })}
            />
            <aside
              id="mq-inspector"
              aria-label="Inspector"
              data-region="inspector"
              className="flex min-h-0 shrink-0 flex-col border-l border-default bg-surface"
              style={{ width: inspectorSize }}
            >
              <Inspector />
            </aside>
          </>
        )}
      </div>
      <Notices />
      <CommandPalette />
      <QuickOpen />
      <ConflictDialog />
      <NewElementHost />
    </div>
  );
}
