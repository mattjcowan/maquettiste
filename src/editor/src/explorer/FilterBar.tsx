// The explorer's filter bar (explorer-redesign.md 3.1, 3.2): the search box, the chips menu (kind, domain scope,
// tag, category, stereotype, has errors, on this diagram), the scope picker (the user's scopes and the team's from
// maquettiste.json `explorer.scopes`), the pin that keeps the filter across reloads, and the active chips.
import { useMemo, useState, type KeyboardEvent, type RefObject } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Bookmark, Filter, Pin, PinOff, X } from "lucide-react";
import { keys, useElements, useSettings } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ElementSummary, SettingsJson } from "@/api/types";
import { categoryOptions, chipLabel, filterVocabularies, tagOptions } from "@/model/vocabularies";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/menu";
import { useServices } from "@/app/context";
import { useEditor } from "@/state/store";
import { KIND_LABELS, SEARCH_PLACEHOLDER } from "@/model/labels";
import { displayName } from "@/model/model";
import { placeOf } from "@/search/engine";
import { chipsOf, withoutQualifier } from "@/search/query";
import { applyScope, chipCount, sameChips, scopeOf, type ExplorerFilter, type FilterScope } from "./filter";
import type { ExplorerId } from "./tree";

type ListKey = "kinds" | "tags" | "categories" | "stereotypes";

const kindLabel = (kind: string) => KIND_LABELS[kind as keyof typeof KIND_LABELS] ?? kind;

/** Team scopes as the settings carry them (older servers leave them out). */
export function teamScopes(json: SettingsJson | undefined): FilterScope[] {
  const list = (json?.explorer as { scopes?: Partial<FilterScope>[] } | undefined)?.scopes ?? [];
  return list.filter((s) => !!s.name).map((s) => scopeOf(s.name!, applyScope({ text: "" } as ExplorerFilter, s)));
}

export function FilterBar({
  id,
  filter,
  rows,
  setFilter,
  inputRef,
  onKeyDown,
}: {
  id: ExplorerId;
  filter: ExplorerFilter;
  rows: readonly ElementSummary[] | undefined;
  setFilter: (f: ExplorerFilter) => void;
  inputRef: RefObject<HTMLInputElement | null>;
  onKeyDown: (e: KeyboardEvent<HTMLInputElement>) => void;
}) {
  const { store } = useServices();
  const pinned = useEditor(store, (s) => s.explorer.views[id].pinnedFilter);
  const mine = useEditor(store, (s) => s.explorer.scopes);
  const activeDiagram = useEditor(store, (s) => s.activeDiagram);
  const settings = useSettings();
  const team = useMemo(() => teamScopes(settings.data?.json), [settings.data]);
  const [saving, setSaving] = useState(false);
  const typed = chipsOf(filter.text);
  const count = chipCount(filter);

  // Tags and categories (1.11): the global vocabularies and, with a domain chip, that domain's chain nearest first;
  // without one, every domain's too. A domain entry names its domain ("core · Billing").
  const tagVocabularies = useMemo(() => filterVocabularies("tag-vocabulary", filter.domain, rows ?? []), [rows, filter.domain]);
  const categoryVocabularies = useMemo(() => filterVocabularies("category-tree", filter.domain, rows ?? []), [rows, filter.domain]);
  const vocabularyDocs = useElements(useMemo(() => [...tagVocabularies, ...categoryVocabularies].map((v) => v.id), [tagVocabularies, categoryVocabularies]));
  const categories = useMemo(() => categoryOptions(categoryVocabularies, (v) => vocabularyDocs.byId.get(v)?.json), [categoryVocabularies, vocabularyDocs]);
  // Every tag in use, once per index (the documents below change on every selection; the rows do not).
  const usedTags = useMemo(() => [...new Set((rows ?? []).flatMap((r) => r.tags))].sort(), [rows]);
  const tags = useMemo(() => {
    const declared = tagOptions(tagVocabularies, (v) => vocabularyDocs.byId.get(v)?.json).options;
    const known = new Set(declared.map((t) => t.value));
    // A tag in use but declared nowhere on the chain is still offered, bare.
    const undeclared = usedTags.filter((t) => !known.has(t));
    return [...declared, ...undeclared.map((t) => ({ value: t, label: t }))];
  }, [tagVocabularies, vocabularyDocs, usedTags]);
  const stereotypes = useMemo(() => [...new Set((rows ?? []).flatMap((r) => r.stereotypes))].sort(), [rows]);
  const byId = useMemo(() => new Map((rows ?? []).map((r) => [r.id, r])), [rows]);
  // The kinds this explorer lists (tables are summaries, not index rows).
  const kinds = useMemo(() => {
    const set = new Set((rows ?? []).map((r) => r.kind).filter((k) => placeOf(k) === id));
    if (id === "databases") set.add("table");
    return [...set].sort((a, b) => kindLabel(a).localeCompare(kindLabel(b)));
  }, [rows, id]);
  const domains = useMemo(() => {
    const path = (r: ElementSummary): string => {
      const names: string[] = [];
      for (let d: ElementSummary | undefined = r, guard = 0; d && guard < 64; d = d.package ? byId.get(d.package) : undefined, guard++)
        names.push(displayName(d));
      return names.reverse().join(" › ");
    };
    return (rows ?? [])
      .filter((r) => r.kind === "package")
      .map((r) => ({ id: r.id, label: path(r) }))
      .sort((a, b) => a.label.localeCompare(b.label));
  }, [rows, byId]);

  const toggleIn = (key: ListKey, value: string) =>
    setFilter({ ...filter, [key]: filter[key].includes(value) ? filter[key].filter((v) => v !== value) : [...filter[key], value] });
  const domainLabel = (d: string) => domains.find((x) => x.id === d)?.label ?? d;
  const diagramName = filter.diagram ? (byId.get(filter.diagram)?.name ?? "diagram") : "";
  const chips: { key: string; label: string; remove: () => void }[] = [
    ...filter.kinds.map((v) => ({ key: `k:${v}`, label: kindLabel(v), remove: () => toggleIn("kinds", v) })),
    ...(filter.domain ? [{ key: "d", label: `in ${domainLabel(filter.domain)}`, remove: () => setFilter({ ...filter, domain: null }) }] : []),
    ...filter.tags.map((v) => ({ key: `t:${v}`, label: `#${chipLabel(v, tags)}`, remove: () => toggleIn("tags", v) })),
    ...filter.categories.map((v) => ({ key: `c:${v}`, label: chipLabel(v, categories), remove: () => toggleIn("categories", v) })),
    ...filter.stereotypes.map((v) => ({ key: `s:${v}`, label: `«${v}»`, remove: () => toggleIn("stereotypes", v) })),
    ...(filter.errors ? [{ key: "e", label: "Has errors", remove: () => setFilter({ ...filter, errors: false }) }] : []),
    ...(filter.diagram ? [{ key: "g", label: `On ${diagramName}`, remove: () => setFilter({ ...filter, diagram: null }) }] : []),
  ];
  const inUse = (s: FilterScope) => count > 0 && sameChips(filter, applyScope(filter, s));

  return (
    <div className="flex flex-col gap-2 border-b border-default p-2" data-testid="explorer-filter-bar">
      <div className="flex items-center gap-1">
        <Input
          type="search"
          placeholder={SEARCH_PLACEHOLDER}
          aria-label={SEARCH_PLACEHOLDER}
          ref={inputRef}
          value={filter.text}
          onChange={(e) => setFilter({ ...filter, text: e.target.value })}
          onKeyDown={onKeyDown}
          data-region-focus
        />
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label="Scopes" data-testid="explorer-scopes">
              <Bookmark />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="max-h-96 overflow-auto">
            <DropdownMenuLabel>Your scopes</DropdownMenuLabel>
            {mine.length === 0 ? <p className="px-2 py-1 text-12 text-secondary">None saved</p> : null}
            {mine.map((s) => (
              <DropdownMenuCheckboxItem key={`mine:${s.name}`} checked={inUse(s)} onCheckedChange={() => setFilter(applyScope(filter, s))}>
                {s.name}
              </DropdownMenuCheckboxItem>
            ))}
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Team scopes</DropdownMenuLabel>
            {team.length === 0 ? <p className="px-2 py-1 text-12 text-secondary">None in the project settings</p> : null}
            {team.map((s) => (
              <DropdownMenuCheckboxItem key={`team:${s.name}`} checked={inUse(s)} onCheckedChange={() => setFilter(applyScope(filter, s))}>
                {s.name}
              </DropdownMenuCheckboxItem>
            ))}
            <DropdownMenuSeparator />
            <DropdownMenuItem disabled={count === 0} onSelect={() => setSaving(true)}>
              Save the filters as a scope…
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={`Filters${count ? ` (${count} active)` : ""}`} data-testid="explorer-filters" className="relative">
              <Filter />
              {count ? (
                <span aria-hidden className="absolute -right-0.5 -top-0.5 rounded-full bg-accent px-1 text-11 leading-4 text-accent-foreground">
                  {count}
                </span>
              ) : null}
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="max-h-96 overflow-auto">
            <DropdownMenuCheckboxItem checked={filter.errors} onCheckedChange={(on) => setFilter({ ...filter, errors: !!on })}>
              Has errors
            </DropdownMenuCheckboxItem>
            <DropdownMenuCheckboxItem
              checked={!!filter.diagram}
              disabled={!activeDiagram && !filter.diagram}
              onCheckedChange={(on) => setFilter({ ...filter, diagram: on ? activeDiagram : null })}
            >
              On this diagram
            </DropdownMenuCheckboxItem>
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Kinds</DropdownMenuLabel>
            {kinds.map((k) => (
              <DropdownMenuCheckboxItem key={k} checked={filter.kinds.includes(k)} onCheckedChange={() => toggleIn("kinds", k)}>
                {kindLabel(k)}
              </DropdownMenuCheckboxItem>
            ))}
            {domains.length ? (
              <>
                <DropdownMenuSeparator />
                <DropdownMenuLabel>Domain</DropdownMenuLabel>
                {domains.map((d) => (
                  <DropdownMenuCheckboxItem
                    key={d.id}
                    checked={filter.domain === d.id}
                    onCheckedChange={() => setFilter({ ...filter, domain: filter.domain === d.id ? null : d.id })}
                  >
                    {d.label}
                  </DropdownMenuCheckboxItem>
                ))}
              </>
            ) : null}
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Tags</DropdownMenuLabel>
            {tags.length === 0 ? <p className="px-2 py-1 text-12 text-secondary">No tags</p> : null}
            {tags.map((t) => (
              <DropdownMenuCheckboxItem key={t.value} checked={filter.tags.includes(t.value)} onCheckedChange={() => toggleIn("tags", t.value)}>
                {t.label}
              </DropdownMenuCheckboxItem>
            ))}
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Categories</DropdownMenuLabel>
            {categories.map((c) => (
              <DropdownMenuCheckboxItem key={c.value} checked={filter.categories.includes(c.value)} onCheckedChange={() => toggleIn("categories", c.value)}>
                {c.label}
              </DropdownMenuCheckboxItem>
            ))}
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Stereotypes</DropdownMenuLabel>
            {stereotypes.map((s) => (
              <DropdownMenuCheckboxItem key={s} checked={filter.stereotypes.includes(s)} onCheckedChange={() => toggleIn("stereotypes", s)}>
                {s}
              </DropdownMenuCheckboxItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      {count > 0 || typed.length > 0 || pinned ? (
        <div className="flex flex-wrap gap-1" aria-label="Active filters">
          {count > 0 || pinned ? (
            <button
              type="button"
              onClick={() => store.getState().pinExplorerFilter(id, !pinned)}
              className="inline-flex h-6 items-center gap-1 rounded-control border border-default px-1.5 text-12"
              aria-label={pinned ? "Unpin the filter" : "Pin the filter"}
              aria-pressed={pinned}
              data-testid="explorer-pin-filter"
            >
              {pinned ? <PinOff className="size-3" aria-hidden /> : <Pin className="size-3" aria-hidden />}
              {pinned ? "Pinned" : "Pin"}
            </button>
          ) : null}
          {typed.map((chip) => (
            <button
              key={`q:${chip.key}:${chip.value}`}
              type="button"
              onClick={() => setFilter({ ...filter, text: withoutQualifier(filter.text, chip.key, chip.value) })}
              className="inline-flex h-6 items-center gap-1 rounded-control border border-accent bg-accent-subtle px-1.5 text-12"
              aria-label={`Remove filter ${chip.label}`}
            >
              {chip.label}
              <X className="size-3" aria-hidden />
            </button>
          ))}
          {chips.map((chip) => (
            <button
              key={chip.key}
              type="button"
              onClick={chip.remove}
              className="inline-flex h-6 items-center gap-1 rounded-control border border-accent bg-accent-subtle px-1.5 text-12"
              aria-label={`Remove filter ${chip.label}`}
            >
              {chip.label}
              <X className="size-3" aria-hidden />
            </button>
          ))}
        </div>
      ) : null}
      <SaveScopeDialog open={saving} filter={filter} onClose={() => setSaving(false)} />
    </div>
  );
}

/** Saves the chips under a name: for the user (localStorage) or, through the project settings, for the team. */
function SaveScopeDialog({ open, filter, onClose }: { open: boolean; filter: ExplorerFilter; onClose: () => void }) {
  const { store } = useServices();
  const queryClient = useQueryClient();
  const settings = useSettings();
  const [name, setName] = useState("");
  const [shared, setShared] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const close = () => {
    setName("");
    setShared(false);
    setError(null);
    onClose();
  };
  const save = async () => {
    const n = name.trim();
    if (!n) return;
    if (!shared) {
      store.getState().saveScope(n, filter);
      close();
      return;
    }
    const doc = settings.data;
    if (!doc) return;
    const json = structuredClone(doc.json) as SettingsJson;
    const explorer = (json.explorer ?? { folders: [] }) as { folders: unknown[]; scopes?: FilterScope[] };
    explorer.scopes = [...(explorer.scopes ?? []).filter((s) => s.name !== n), scopeOf(n, filter)];
    (json as { explorer?: unknown }).explorer = explorer;
    const result = await endpoints.saveSettings(json, doc.hash);
    if (result.outcome !== "saved") {
      setError(result.diagnostics[0]?.message ?? `The settings were not saved (${result.outcome}).`);
      return;
    }
    await queryClient.invalidateQueries({ queryKey: keys.settings });
    close();
  };
  return (
    <Dialog open={open} onOpenChange={(o) => !o && close()}>
      {open ? (
        <DialogContent title="Save the filters as a scope" description="A scope keeps the chips, not the search text.">
          <form
            className="flex flex-col gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              void save();
            }}
          >
            <Input aria-label="Scope name" placeholder="Scope name" value={name} onChange={(e) => setName(e.target.value)} autoFocus data-testid="scope-name" />
            <CheckboxField id="scope-shared" label="Share with the team (project settings)" checked={shared} onChange={setShared} />
            {error ? <p className="text-12 text-danger">{error}</p> : null}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={close}>
                Cancel
              </Button>
              <Button type="submit" disabled={!name.trim()} data-testid="scope-save">
                Save
              </Button>
            </div>
          </form>
        </DialogContent>
      ) : null}
    </Dialog>
  );
}
