// Favorites and recents (explorer-redesign.md 3.3): a collapsible strip above the tree with the user's starred
// elements and the last 20 opened ones, both kept per user in localStorage. Each explorer lists the ones it holds; each section
// has Clear in its header (favorites ask first) and each row a Remove. A deleted element leaves the recent list. A row's
// click opens the element as its tree row's double click does (its editor, pinned), not only selects it.
import { useState } from "react";
import { ChevronDown, ChevronRight, Eraser, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { ElementSummary } from "@/api/types";
import { useServices } from "@/app/context";
import { local } from "@/lib/storage";
import { displayName } from "@/model/model";
import { useEditor } from "@/state/store";
import type { ExplorerId, Forest } from "./tree";

const RECENT_SHOWN = 20;

/** The ids of a list that this explorer lists, in list order, at most `limit`. */
export function inExplorer(forest: Forest, id: ExplorerId, ids: readonly string[], limit = Infinity): string[] {
  const out: string[] = [];
  for (const x of ids) {
    if (out.length >= limit) break;
    const key = forest.place.get(x);
    if (key && forest.nodes.get(key)?.explorer === id && forest.byId.has(x)) out.push(x);
  }
  return out;
}

export function FavoritesStrip({ forest, id, onOpen }: { forest: Forest; id: ExplorerId; onOpen: (key: string) => void }) {
  const { store } = useServices();
  const favorites = useEditor(store, (s) => s.explorer.favorites);
  const recent = useEditor(store, (s) => s.recent);
  const [open, setOpen] = useState(() => local.get("mq.explorer.strip") !== "0");
  const fav = inExplorer(forest, id, favorites);
  const rec = inExplorer(forest, id, recent, RECENT_SHOWN);
  if (!fav.length && !rec.length) return null;
  const toggle = () => {
    local.set("mq.explorer.strip", open ? "0" : "1");
    setOpen(!open);
  };
  const item = (x: string, group: "favorite" | "recent") => {
    const row = forest.byId.get(x) as ElementSummary;
    const name = displayName(row);
    return (
      <li key={`${group}:${x}`} className="group flex items-center">
        <button
          type="button"
          className="min-w-0 flex-1 truncate rounded-control px-2 py-0.5 text-left text-12 hover:bg-app"
          onClick={() => onOpen(forest.place.get(x) as string)}
          data-testid={`explorer-${group}-${row.name}`}
        >
          {name}
        </button>
        <Button
          size="icon-row"
          variant="ghost"
          className="opacity-0 focus-visible:opacity-100 group-hover:opacity-100"
          label={group === "favorite" ? `Remove ${name} from the favorites` : `Remove ${name} from the recent list`}
          onClick={() => (group === "favorite" ? store.getState().clearFavorites([x]) : store.getState().clearRecent([x]))}
          data-testid={`explorer-${group}-remove-${row.name}`}
        >
          <X />
        </Button>
      </li>
    );
  };
  const header = (title: string, testid: string, tooltip: string, onClear: () => void) => (
    <div className="flex items-center gap-1 px-2">
      <h3 className="min-w-0 flex-1 text-11 text-secondary">{title}</h3>
      <Button size="icon-row" variant="ghost" label={tooltip} onClick={onClear} data-testid={testid}>
        <Eraser />
      </Button>
    </div>
  );
  return (
    <div className="border-b border-default px-1 py-1" data-testid="explorer-strip">
      <button type="button" className="flex items-center gap-1 px-1 text-11 font-medium uppercase text-secondary" aria-expanded={open} onClick={toggle}>
        {open ? <ChevronDown className="size-3" aria-hidden /> : <ChevronRight className="size-3" aria-hidden />}
        Favorites and recent
      </button>
      {open ? (
        <div className="grid grid-cols-1 gap-1 pt-1">
          {fav.length ? (
            <section aria-label="Favorites">
              {header("Favorites", "explorer-favorites-clear", "Clear the favorites listed here", () => {
                // Starred elements are kept on purpose: clearing them asks first.
                if (window.confirm(`Remove ${fav.length === 1 ? "this favorite" : `these ${fav.length} favorites`}?`)) store.getState().clearFavorites(fav);
              })}
              <ul className="max-h-28 overflow-auto">{fav.map((x) => item(x, "favorite"))}</ul>
            </section>
          ) : null}
          {rec.length ? (
            <section aria-label="Recent">
              {header("Recent", "explorer-recent-clear", "Clear the recent elements listed here", () => store.getState().clearRecent(rec))}
              <ul className="max-h-28 overflow-auto">{rec.map((x) => item(x, "recent"))}</ul>
            </section>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
