// Breadcrumbs above the canvas and the editors (explorer-redesign.md 3.3): `Domain model › Sales › Orders › Entities ›
// Order` for the selected element. Each segment opens a menu of its siblings. The back and forward buttons walk the
// selection history (Alt+Left, Alt+Right).
import { useMemo } from "react";
import { ArrowLeft, ArrowRight, ChevronRight } from "lucide-react";
import { useIndex } from "@/api/queries";
import type { ElementSummary } from "@/api/types";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from "@/components/ui/menu";
import { EXPLORER_LABELS, kindFolder, OTHER_FOLDER } from "@/model/labels";
import { placeOf } from "@/search/engine";
import { useEditor } from "@/state/store";
import { useServices } from "./context";
import { useEditorNavigation } from "./navigation";

export interface Crumb {
  key: string;
  label: string;
  /** The element the segment stands for (a domain, a database or the element itself). */
  id?: string;
  /** The segment's siblings, for its menu: elements to go to. */
  siblings: { id: string; label: string }[];
}

const MAX_SIBLINGS = 200;
const SETTINGS_LABEL = "Settings";

const byName = (a: ElementSummary, b: ElementSummary) => a.name.localeCompare(b.name);
const entry = (r: ElementSummary) => ({ id: r.id, label: r.name });

/** The breadcrumb path of an element in the index, or [] when it is not there. */
export function breadcrumbPath(id: string, rows: readonly ElementSummary[], byId: ReadonlyMap<string, ElementSummary>): Crumb[] {
  const row = byId.get(id);
  if (!row) return [];
  const place = placeOf(row.kind);
  const out: Crumb[] = [{ key: "place", label: place === "settings" ? SETTINGS_LABEL : EXPLORER_LABELS[place], siblings: [] }];
  const siblingsWhere = (test: (r: ElementSummary) => boolean) => rows.filter(test).sort(byName).slice(0, MAX_SIBLINGS).map(entry);
  if (place === "databases" && row.database && row.kind !== "database") {
    const db = byId.get(row.database);
    if (db) out.push({ key: `db:${db.id}`, label: db.name, id: db.id, siblings: siblingsWhere((r) => r.kind === "database") });
  }
  // The domain chain, outermost first (cycle-safe). A domain's own parent is its `package`.
  const chain: ElementSummary[] = [];
  const seen = new Set<string>([row.id]);
  for (let p = row.package ? byId.get(row.package) : undefined; p && !seen.has(p.id); p = p.package ? byId.get(p.package) : undefined) {
    seen.add(p.id);
    chain.unshift(p);
  }
  if (place === "domain-model" || row.kind === "package")
    for (const d of chain)
      out.push({
        key: `domain:${d.id}`,
        label: d.name,
        id: d.id,
        siblings: siblingsWhere((r) => r.kind === "package" && (r.package ?? null) === (d.package ?? null)),
      });
  const pkg = row.package ?? null;
  const sameFolder = (r: ElementSummary) => r.kind === row.kind && (r.package ?? null) === pkg && (r.database ?? null) === (row.database ?? null);
  const folder = kindFolder(row.kind) ?? OTHER_FOLDER;
  if (row.kind !== "package" && place !== "settings" && folder.label !== out[0].label) {
    out.push({ key: `folder:${row.kind}`, label: folder.label, siblings: siblingsWhere(sameFolder) });
  }
  out.push({ key: `el:${row.id}`, label: row.name, id: row.id, siblings: siblingsWhere(sameFolder) });
  return out;
}

export function Breadcrumbs() {
  const { store } = useServices();
  const index = useIndex();
  const selection = useEditor(store, (s) => s.selection);
  const activeDiagram = useEditor(store, (s) => s.activeDiagram);
  const history = useEditor(store, (s) => s.history);
  const workspace = useEditor(store, (s) => s.workspace);
  const { goTo, travel } = useEditorNavigation();
  const byId = useMemo(() => new Map((index.data ?? []).map((r) => [r.id, r])), [index.data]);
  const current =
    selection.length === 1
      ? selection[0]
      : !selection.length && workspace === "entities" && activeDiagram && !activeDiagram.startsWith("pkg:")
        ? activeDiagram
        : null;
  const path = useMemo(() => (current && index.data ? breadcrumbPath(current, index.data, byId) : []), [current, index.data, byId]);

  return (
    <nav aria-label="Breadcrumbs" className="flex h-7 shrink-0 items-center gap-1 border-b border-default bg-surface px-2 text-12" data-testid="breadcrumbs">
      <Button
        size="icon-sm"
        variant="ghost"
        aria-label="Back (Alt+Left)"
        title="Back (Alt+Left)"
        disabled={!history.back.length}
        onClick={() => travel("back")}
        data-testid="history-back"
      >
        <ArrowLeft />
      </Button>
      <Button
        size="icon-sm"
        variant="ghost"
        aria-label="Forward (Alt+Right)"
        title="Forward (Alt+Right)"
        disabled={!history.forward.length}
        onClick={() => travel("forward")}
        data-testid="history-forward"
      >
        <ArrowRight />
      </Button>
      {selection.length > 1 ? <span className="px-1 text-secondary">{selection.length} selected</span> : null}
      <ol className="flex min-w-0 items-center gap-0.5">
        {path.map((crumb, i) => (
          <li key={crumb.key} className="flex min-w-0 items-center gap-0.5">
            {i > 0 ? <ChevronRight className="size-3 shrink-0 text-secondary" aria-hidden /> : null}
            {crumb.siblings.length ? (
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <button
                    type="button"
                    className="max-w-48 truncate rounded-control px-1 hover:bg-app aria-[current=page]:font-medium"
                    aria-current={i === path.length - 1 ? "page" : undefined}
                    data-testid={`crumb-${crumb.label}`}
                  >
                    {crumb.label}
                  </button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="start" className="max-h-[min(20rem,var(--radix-dropdown-menu-content-available-height))]">
                  {crumb.id ? <DropdownMenuItem onSelect={() => goTo(crumb.id!)}>Go to {crumb.label}</DropdownMenuItem> : null}
                  <DropdownMenuLabel>{crumb.id ? "Siblings" : crumb.label}</DropdownMenuLabel>
                  {crumb.siblings.map((s) => (
                    <DropdownMenuItem key={s.id} onSelect={() => goTo(s.id)}>
                      {s.label}
                    </DropdownMenuItem>
                  ))}
                </DropdownMenuContent>
              </DropdownMenu>
            ) : (
              <span className="truncate px-1 text-secondary">{crumb.label}</span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}
