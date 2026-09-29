// The centre area's tabs (explorer-redesign.md 3.6): the screen (the canvas, the database, ...) first, then one tab
// per open element editor. The preview tab is in italics and replaced by the next single click; a double click on it,
// an edit, Enter or a double click in the explorer pins it. A middle click or the cross closes a tab; a dot marks one
// with unsaved changes. "Follow selection" turns the shown tab into the General-mode editor.
import { useEffect, type MouseEvent } from "react";
import { Crosshair, X } from "lucide-react";
import { useIndex } from "@/api/queries";
import { cn } from "@/lib/cn";
import { useServices } from "@/app/context";
import { KindIcon } from "@/app/icons";
import { useEditor, type Workspace } from "@/state/store";
import { indexLookup } from "@/model/index";
import { displayName } from "@/model/model";
import { SCREEN_LABELS } from "@/model/labels";
import { activate, activeTab, closeElement, closeTab, followSelection, hasEditor, pinTab, setFollow, type EditorTab } from "./tabs";
import { EntityEditor } from "./EntityEditor";
import { RelationshipEditor } from "./RelationshipEditor";
import { TypeEditor } from "./TypeEditor";
import { DomainEditor } from "./DomainEditor";

/** A draft that is not yet saved as it shows: the tab's dirty marker. */
const UNSAVED = new Set(["dirty", "saving", "invalid", "conflict"]);

export function EditorTabBar({ workspace }: { workspace: Workspace }) {
  const { store } = useServices();
  const editors = useEditor(store, (s) => s.editors);
  const drafts = useEditor(store, (s) => s.drafts);
  const index = useIndex();
  const lookup = indexLookup(index.data);
  if (!editors.tabs.length) return null;
  const update = store.getState().updateEditors;
  const shown = activeTab(editors);
  const closeOnMiddle = (tab: EditorTab) => (e: MouseEvent) => {
    if (e.button !== 1) return;
    e.preventDefault();
    update((s) => closeTab(s, tab.key));
  };
  return (
    <div className="flex h-8 shrink-0 items-stretch border-b border-default bg-surface" data-testid="editor-tabs">
      {/* A toolbar, not a tablist: each tab carries its own close button. */}
      <div role="toolbar" aria-label="Open editors" className="flex min-w-0 flex-1 items-stretch overflow-x-auto">
        <button
          type="button"
          aria-current={editors.active === null ? "true" : undefined}
          className={cn(
            "flex shrink-0 items-center border-r border-default px-3 text-12",
            editors.active === null ? "bg-canvas font-medium text-primary" : "text-secondary hover:text-primary",
          )}
          onClick={() => update((s) => activate(s, null))}
          data-testid="editor-tab-screen"
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
              data-testid="editor-tab"
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
              >
                <KindIcon kind={tab.kind} />
                <span className="max-w-40 truncate">{name}</span>
                {tab.follow ? <Crosshair className="size-3 text-accent" aria-label="Follows the selection" /> : null}
                {dirty ? <span className="size-2 rounded-full bg-accent" role="img" aria-label="Unsaved changes" data-testid="editor-tab-dirty" /> : null}
              </button>
              <button
                type="button"
                aria-label={`Close ${name}`}
                className="rounded-[3px] p-0.5 opacity-60 hover:bg-accent-subtle hover:opacity-100"
                onClick={() => update((s) => closeTab(s, tab.key))}
              >
                <X className="size-3" />
              </button>
            </div>
          );
        })}
      </div>
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
    if (row && hasEditor(kind)) store.getState().updateEditors((s) => followSelection(s, { id: row.id, kind }));
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
        <EntityEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "relation" ? (
        <RelationshipEditor key={tab.id} id={tab.id} />
      ) : tab.kind === "package" ? (
        <DomainEditor key={tab.id} id={tab.id} />
      ) : (
        <TypeEditor key={tab.id} id={tab.id} kind={tab.kind} />
      )}
    </div>
  );
}
