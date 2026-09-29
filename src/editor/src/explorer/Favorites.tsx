// Favorites and recents (explorer-redesign.md 3.3): a collapsible strip above the tree with the user's starred
// elements and the last 20 opened ones, both kept per user in localStorage. Each explorer lists the ones it holds.
import { useState } from "react";
import { ChevronDown, ChevronRight } from "lucide-react";
import type { ElementSummary } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
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

export function FavoritesStrip({ forest, id }: { forest: Forest; id: ExplorerId }) {
  const { store } = useServices();
  const favorites = useEditor(store, (s) => s.explorer.favorites);
  const recent = useEditor(store, (s) => s.recent);
  const { reveal } = useEditorNavigation();
  const [open, setOpen] = useState(() => local.get("mq.explorer.strip") !== "0");
  const fav = inExplorer(forest, id, favorites);
  const rec = inExplorer(forest, id, recent, RECENT_SHOWN);
  if (!fav.length && !rec.length) return null;
  const toggle = () => {
    local.set("mq.explorer.strip", open ? "0" : "1");
    setOpen(!open);
  };
  const item = (x: string, group: string) => {
    const row = forest.byId.get(x) as ElementSummary;
    return (
      <li key={`${group}:${x}`}>
        <button
          type="button"
          className="w-full truncate rounded-control px-2 py-0.5 text-left text-12 hover:bg-app"
          onClick={() => reveal(row)}
          data-testid={`explorer-${group}-${row.name}`}
        >
          {displayName(row)}
        </button>
      </li>
    );
  };
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
              <h3 className="px-2 text-11 text-secondary">Favorites</h3>
              <ul className="max-h-28 overflow-auto">{fav.map((x) => item(x, "favorite"))}</ul>
            </section>
          ) : null}
          {rec.length ? (
            <section aria-label="Recent">
              <h3 className="px-2 text-11 text-secondary">Recent</h3>
              <ul className="max-h-28 overflow-auto">{rec.map((x) => item(x, "recent"))}</ul>
            </section>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
