// The centre area's tabs (explorer-redesign.md 3.6): the screen (the canvas, the database, ...) first, then one tab
// per open element editor. The preview tab is in italics and replaced by the next single click; a double click on it,
// an edit, Enter or a double click in the explorer pins it. A middle click or the cross closes a tab; a dot marks one
// with unsaved changes. "Follow selection" turns the shown tab into the General-mode editor. Many tabs scroll inside the
// strip without a scrollbar (arrows at the ends, the wheel, the shown tab kept in view); a right click, the menu key or
// Shift+F10 on a tab opens its menu (Close, Close others, Close to the right, Close all, Close saved, Pin or Unpin).
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactNode } from "react";
import { iconLabel } from "@/components/ui/button";
import { ChevronLeft, ChevronRight, Crosshair, X } from "lucide-react";
import { useIndex } from "@/api/queries";
import { cn } from "@/lib/cn";
import { useServices } from "@/app/context";
import { KindIcon } from "@/app/icons";
import { EdgeToggle, PanelToggle } from "@/app/panels";
import { RowMenu, type RowMenuItem } from "@/explorer/RowMenu";
import { useEditor, type Workspace } from "@/state/store";
import { indexLookup } from "@/model/index";
import { displayName } from "@/model/model";
import { SCREEN_LABELS } from "@/model/labels";
import {
  activate,
  activeTab,
  closeAll,
  closeElement,
  closeOthers,
  closeSaved,
  closeTab,
  closeToRight,
  followSelection,
  hasEditor,
  pinTab,
  setFollow,
  unpinTab,
  type EditorTab,
  type EditorTabsState,
  type KeepTab,
} from "./tabs";
import { EntityEditor } from "./EntityEditor";
import { RelationshipEditor } from "./RelationshipEditor";
import { TypeEditor } from "./TypeEditor";
import { DomainEditor } from "./DomainEditor";
import { ProcessEditor } from "./process/ProcessEditor";
import { ActorEditor } from "./actor/ActorEditor";
import { ScenarioEditor } from "./scenario/ScenarioEditor";
import { TableEditor } from "./database/TableEditor";
import { ViewEditor } from "./database/ViewEditor";
import { SequenceEditor } from "./database/SequenceEditor";
import { RoutineEditor } from "./database/RoutineEditor";
import { DatabaseTypeEditor } from "./database/DatabaseTypeEditor";
import { SqlObjectEditor } from "./database/SqlObjectEditor";
import { QueryEditor } from "./database/QueryEditor";

/** A draft that is not yet saved as it shows: the tab's dirty marker. */
const UNSAVED = new Set(["dirty", "saving", "invalid", "conflict"]);

type TabAction = "close" | "close-others" | "close-right" | "close-all" | "close-saved" | "pin" | "unpin";

/** The bulk closes: each leaves the tabs `keep` holds (unsaved changes) open. */
const BULK: Partial<Record<TabAction, (state: EditorTabsState, key: string, keep: KeepTab) => EditorTabsState>> = {
  "close-others": closeOthers,
  "close-right": closeToRight,
  "close-all": (state, _key, keep) => closeAll(state, keep),
  "close-saved": (state, _key, keep) => closeSaved(state, keep),
};

/** A tab's menu; an item that would do nothing is disabled. */
function tabMenuItems(state: EditorTabsState, tab: EditorTab, dirty: KeepTab): RowMenuItem<TabAction>[] {
  const idle = (action: TabAction) => BULK[action]!(state, tab.key, dirty) === state;
  return [
    { id: "close", label: "Close" },
    { id: "close-others", label: "Close others", disabled: idle("close-others") },
    { id: "close-right", label: "Close to the right", disabled: idle("close-right") },
    { id: "close-all", label: "Close all", disabled: idle("close-all") },
    { id: "close-saved", label: "Close saved", disabled: idle("close-saved") },
    tab.pinned ? { id: "unpin", label: "Unpin", separator: true, disabled: tab.follow } : { id: "pin", label: "Pin", separator: true },
  ];
}

/** Runs a tab menu action; a bulk close (but Close saved, which says it) reports the tabs it kept for their unsaved changes. */
function runTabAction(state: EditorTabsState, action: TabAction, key: string, dirty: KeepTab): { state: EditorTabsState; kept: number } {
  if (action === "close") return { state: closeTab(state, key), kept: 0 };
  if (action === "pin") return { state: pinTab(state, key), kept: 0 };
  if (action === "unpin") return { state: unpinTab(state, key), kept: 0 };
  const close = BULK[action]!;
  const next = close(state, key, dirty);
  const kept = action === "close-saved" ? 0 : next.tabs.length - close(state, key, () => false).tabs.length;
  return { state: next, kept };
}

interface TabMenu {
  x: number;
  y: number;
  key: string;
  title: string;
  items: RowMenuItem<TabAction>[];
  /** Where focus goes back when the menu closes. */
  origin: HTMLElement | null;
}

export function EditorTabBar({ workspace }: { workspace: Workspace }) {
  const { store } = useServices();
  const editors = useEditor(store, (s) => s.editors);
  const drafts = useEditor(store, (s) => s.drafts);
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const collapsed = useEditor(store, (s) => s.tabsCollapsed);
  const [menu, setMenu] = useState<TabMenu | null>(null);
  if (!editors.tabs.length) return null;
  // Hidden: a slim strip along the top of the centre area brings the tabs back.
  if (collapsed) return <EdgeToggle panel="tabs" side="top" />;
  const update = store.getState().updateEditors;
  const shown = activeTab(editors);
  const isDirty: KeepTab = (t) => UNSAVED.has(store.getState().drafts[t.id]?.status ?? "");
  const closeOnMiddle = (tab: EditorTab) => (e: MouseEvent) => {
    if (e.button !== 1) return;
    e.preventDefault();
    update((s) => closeTab(s, tab.key));
  };
  const openMenu = (tab: EditorTab, title: string, at: { x: number; y: number }, origin: HTMLElement | null) =>
    setMenu({ ...at, key: tab.key, title, items: tabMenuItems(store.getState().editors, tab, isDirty), origin });
  const menuAtTab = (tab: EditorTab, title: string, button: HTMLElement) => {
    const box = button.getBoundingClientRect();
    openMenu(tab, title, { x: box.left + 8, y: box.bottom - 2 }, button);
  };
  const run = (action: TabAction, key: string) => {
    const s = store.getState();
    const result = runTabAction(s.editors, action, key, isDirty);
    update(() => result.state);
    if (result.kept) s.notify(`${result.kept} ${result.kept === 1 ? "tab" : "tabs"} kept: unsaved changes`);
  };
  return (
    <div className="flex h-7 shrink-0 items-stretch border-b border-default bg-surface" data-testid="editor-tabs">
      <TabStrip active={editors.active}>
        <button
          type="button"
          aria-current={editors.active === null ? "true" : undefined}
          className={cn(
            "flex shrink-0 items-center border-r border-default px-2 text-12",
            editors.active === null ? "bg-canvas font-medium text-primary" : "text-secondary hover:text-primary",
          )}
          onClick={() => update((s) => activate(s, null))}
          data-testid="editor-tab-screen"
          data-tab-key=""
        >
          {SCREEN_LABELS[workspace]}
        </button>
        {editors.tabs.map((tab) => {
          const summary = lookup.byId.get(tab.id);
          const name = displayName(summary ?? { id: tab.id, kind: tab.kind, name: "" });
          const dirty = UNSAVED.has(drafts[tab.id]?.status ?? "");
          const selected = editors.active === tab.key;
          return (
            <div
              key={tab.key}
              className={cn(
                "group flex shrink-0 items-center gap-1 border-r border-default pl-2 pr-1 text-12",
                selected ? "bg-canvas text-primary" : "text-secondary hover:text-primary",
              )}
              onMouseDown={(e) => e.button === 1 && e.preventDefault()}
              onAuxClick={closeOnMiddle(tab)}
              onContextMenu={(e) => {
                e.preventDefault();
                const button = e.currentTarget.querySelector<HTMLElement>("button");
                // The menu key fires the event too, at no pointer: the menu then opens at the tab.
                if (e.clientX === 0 && e.clientY === 0 && button) menuAtTab(tab, name, button);
                else openMenu(tab, name, { x: e.clientX, y: e.clientY }, button);
              }}
              data-testid="editor-tab"
              data-tab-key={tab.key}
              data-id={tab.id}
              data-pinned={tab.pinned}
              data-follow={tab.follow}
              data-dirty={dirty}
            >
              <button
                type="button"
                aria-current={selected ? "true" : undefined}
                title={tab.pinned ? undefined : "Preview: the next element you open replaces it. Double-click to keep it open."}
                className={cn("flex items-center gap-1.5 py-1", !tab.pinned && "italic")}
                onClick={() => update((s) => activate(s, tab.key))}
                onDoubleClick={() => update((s) => pinTab(s, tab.key))}
                onKeyDown={(e: KeyboardEvent<HTMLButtonElement>) => {
                  if (e.key !== "ContextMenu" && !(e.key === "F10" && e.shiftKey)) return;
                  e.preventDefault();
                  menuAtTab(tab, name, e.currentTarget);
                }}
              >
                <KindIcon kind={tab.kind} />
                <span className="max-w-40 truncate" data-testid="editor-tab-name">
                  {name}
                </span>
                {tab.follow ? <Crosshair className="size-3 text-accent" aria-label="Follows the selection" /> : null}
                {dirty ? <span className="size-2 rounded-full bg-accent" role="img" aria-label="Unsaved changes" data-testid="editor-tab-dirty" /> : null}
              </button>
              <button
                type="button"
                {...iconLabel(`Close ${name}`)}
                className="rounded-[3px] p-0.5 opacity-60 hover:bg-accent-subtle hover:opacity-100"
                onClick={() => update((s) => closeTab(s, tab.key))}
              >
                <X className="size-3" />
              </button>
            </div>
          );
        })}
      </TabStrip>
      {shown ? (
        <button
          type="button"
          aria-pressed={shown.follow}
          className={cn(
            "flex shrink-0 items-center gap-1 border-l border-default px-2 text-12",
            shown.follow ? "bg-accent-subtle text-primary" : "text-secondary hover:text-primary",
          )}
          title="General mode: the editor follows the selection in the explorer and on the canvas, and keeps its tab"
          onClick={() => update((s) => setFollow(s, shown.key, !shown.follow))}
          data-testid="follow-selection"
        >
          <Crosshair className="size-3.5" aria-hidden /> Follow selection
        </button>
      ) : null}
      <div className="flex shrink-0 items-center border-l border-default px-1">
        <PanelToggle panel="tabs" />
      </div>
      <RowMenu
        menu={menu}
        testId="tab-menu"
        onClose={() => setMenu(null)}
        onCloseAutoFocus={(e) => {
          // Back to the tab the menu came from, or to the shown tab when that one closed.
          e.preventDefault();
          const strip = document.querySelector<HTMLElement>('[data-testid="editor-tabs"]');
          const back = menu?.origin?.isConnected ? menu.origin : strip?.querySelector<HTMLElement>('[aria-current="true"]');
          back?.focus();
        }}
        onRun={(action) => {
          const key = menu?.key;
          setMenu(null);
          if (key) run(action, key);
        }}
      />
    </div>
  );
}

/** The share of the visible width an arrow scrolls the strip by. */
const ARROW_STEP = 0.6;

/** The scrolling part of the tab strip: no scrollbar, so the strip keeps its height. The arrows show over its ends only
 * when tabs hide past that end, without changing its width; a vertical wheel scrolls it sideways; the shown tab is kept
 * in view, clear of the arrows (the strip's scroll padding is an arrow's width). */
function TabStrip({ active, children }: { active: string | null; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const [edges, setEdges] = useState({ left: false, right: false });
  const measure = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    const left = el.scrollLeft > 1;
    const right = el.scrollLeft + el.clientWidth < el.scrollWidth - 1;
    setEdges((e) => (e.left === left && e.right === right ? e : { left, right }));
  }, []);
  // Every render: a tab opened, closed or renamed changes the content's width, which no observer of the strip sees.
  useLayoutEffect(measure);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const resize = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(measure);
    resize?.observe(el);
    const onWheel = (e: WheelEvent) => {
      if (el.scrollWidth <= el.clientWidth || Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
      e.preventDefault();
      el.scrollLeft += e.deltaMode === 1 ? e.deltaY * 16 : e.deltaMode === 2 ? e.deltaY * el.clientWidth : e.deltaY;
    };
    el.addEventListener("scroll", measure, { passive: true });
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => {
      resize?.disconnect();
      el.removeEventListener("scroll", measure);
      el.removeEventListener("wheel", onWheel);
    };
  }, [measure]);
  useEffect(() => {
    const tab = ref.current?.querySelector<HTMLElement>(`[data-tab-key="${active ?? ""}"]`);
    tab?.scrollIntoView?.({ inline: "nearest", block: "nearest" });
  }, [active]);
  const scroll = (direction: -1 | 1) => {
    const el = ref.current;
    if (!el) return;
    const reduce = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
    el.scrollBy?.({ left: direction * el.clientWidth * ARROW_STEP, behavior: reduce ? "auto" : "smooth" });
  };
  // Out of the tab order: the keyboard reaches every tab, and the shown one scrolls into view.
  const arrow = (direction: -1 | 1) => {
    const Icon = direction < 0 ? ChevronLeft : ChevronRight;
    return (
      <button
        type="button"
        tabIndex={-1}
        {...iconLabel(direction < 0 ? "Scroll tabs left" : "Scroll tabs right")}
        className={cn(
          "absolute inset-y-0 flex w-5 items-center justify-center border-default bg-surface text-secondary hover:bg-accent-subtle hover:text-primary",
          direction < 0 ? "left-0 border-r" : "right-0 border-l",
        )}
        onClick={() => scroll(direction)}
        data-testid={direction < 0 ? "tabs-scroll-left" : "tabs-scroll-right"}
      >
        <Icon className="size-3.5" />
      </button>
    );
  };
  return (
    <div className="relative flex min-w-0 flex-1 items-stretch">
      {/* A toolbar, not a tablist: each tab carries its own close button. */}
      <div ref={ref} role="toolbar" aria-label="Open editors" className="mq-no-scrollbar flex min-w-0 flex-1 scroll-px-5 items-stretch overflow-x-auto">
        {children}
      </div>
      {edges.left ? arrow(-1) : null}
      {edges.right ? arrow(1) : null}
    </div>
  );
}

/** The editor shown in front of the screen, when an editor tab is active; keeps General mode in step. */
export function EditorArea() {
  const { store } = useServices();
  const editors = useEditor(store, (s) => s.editors);
  const selection = useEditor(store, (s) => s.selection);
  const index = useIndex();

  // General mode: the following tab shows the element selected alone, when it has an editor.
  useEffect(() => {
    if (selection.length !== 1 || !index.data) return;
    const row = indexLookup(index.data).byId.get(selection[0]);
    const kind = row?.kind;
    if (row && hasEditor(kind, row)) store.getState().updateEditors((s) => followSelection(s, { id: row.id, kind }));
  }, [selection, index.data, store]);

  // A deleted element's tabs close.
  useEffect(() => {
    if (!index.data) return;
    const byId = indexLookup(index.data).byId;
    for (const tab of store.getState().editors.tabs) if (!byId.has(tab.id)) store.getState().updateEditors((s) => closeElement(s, tab.id));
  }, [index.data, store]);

  const tab = activeTab(editors);
  if (!tab) return null;
  return (
    <div className="absolute inset-0 z-10 bg-surface" data-testid="editor-area">
      {tab.kind === "entity" ? (
        // No key: the entity editor stays mounted from one entity to the next (General mode walks entities, §4.5), so
        // React updates its DOM in place instead of rebuilding it; state tied to an entity resets on the id.
        <EntityEditor id={tab.id} />
      ) : tab.kind === "relation" ? (
        <RelationshipEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "package" ? (
        <DomainEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "process" ? (
        <ProcessEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "actor" ? (
        <ActorEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "scenario" ? (
        <ScenarioEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "table" ? (
        <TableEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "view" ? (
        <ViewEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "sequence" ? (
        <SequenceEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "routine" ? (
        <RoutineEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "database-type" ? (
        <DatabaseTypeEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "sql-object" ? (
        <SqlObjectEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "query" ? (
        <QueryEditor key={tab.id} id={tab.id} />
      ) : (
        <TypeEditor key={tab.id} id={tab.id} kind={tab.kind} />
      )}
    </div>
  );
}
