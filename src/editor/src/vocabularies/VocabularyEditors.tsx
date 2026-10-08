// The tag vocabulary and category tree editors (explorer-redesign.md 1.11): the global ones in the Settings screen's
// Tags and Categories tabs, a domain's own in the domain editor's tabs. One of each kind per scope, created on first
// use (the first tag or category added). A domain's tab also lists, read-only, the vocabularies it inherits, nearest
// first ("from Billing", "global"). A key or name that a global or enclosing vocabulary already declares is MQ3021.
import { useMemo, useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, Pencil, Plus, Trash2 } from "lucide-react";
import { applySaveResult, useElements, useIndex } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { CategoryDoc, CategoryTreeDoc, ModelJson, TagVocabularyDoc } from "@/api/types";
import { newId } from "@/lib/ids";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { SectionTitle, Spinner } from "@/components/ui/misc";
import { useDraftDocument } from "@/inspector/useDraft";
import { setOptional } from "@/inspector/fields";
import { KIND_LABELS } from "@/model/labels";
import { isTagKey, ownVocabulary, vocabulariesOnChain, type VocabularyKind } from "@/model/vocabularies";
import { builtInAccent, normalizeHexColor } from "@/design/branding";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { commitRetag } from "./retag";

type Scope = { scope: string | null };

/** The scope's own vocabulary of a kind, as a draft, and a way to create it with its first entry. */
function useScopedVocabulary(kind: VocabularyKind, scope: string | null) {
  const index = useIndex();
  const { queryClient, store } = useServices();
  const rows = index.data;
  const id = useMemo(() => (rows ? (ownVocabulary(kind, scope, rows)?.id ?? null) : null), [rows, kind, scope]);
  const domainName = scope ? (rows?.find((r) => r.id === scope)?.name ?? "domain") : null;
  const create = async (first: Record<string, unknown> | Record<string, unknown>[]) => {
    const entries = Array.isArray(first) ? first : [first];
    const base = kind === "tag-vocabulary" ? "tags" : "categories";
    const name = domainName ? `${kebab(domainName)}-${base}` : base;
    const json = {
      kind,
      id: newId(),
      name,
      ...(scope ? { package: scope } : {}),
      ...(kind === "tag-vocabulary" ? { definitions: entries } : { categories: entries }),
    } as unknown as ModelJson;
    const result = await endpoints.createElement(json);
    if (result.outcome === "saved") applySaveResult(queryClient, result);
    else store.getState().notify(result.diagnostics[0]?.message ?? result.outcome, "error");
  };
  return { id, loading: index.isPending, create, ...useDraftDocument(id) };
}

const kebab = (name: string) =>
  name
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/[^A-Za-z0-9]+/g, "-")
    .replace(/^-|-$/g, "")
    .toLowerCase() || "domain";

/** The vocabularies a domain inherits (its enclosing domains', then the global ones), read-only. */
function Inherited({ kind, scope }: { kind: VocabularyKind; scope: string }) {
  const index = useIndex();
  const chain = useMemo(() => vocabulariesOnChain(kind, scope, index.data ?? []).filter((v) => v.scope !== scope), [index.data, kind, scope]);
  const docs = useElements(chain.map((v) => v.id));
  if (!chain.length) return null;
  const label = kind === "tag-vocabulary" ? "Inherited tags" : "Inherited categories";
  return (
    <section className="flex flex-col gap-1" aria-label={label} data-testid={`inherited-${kind}`}>
      <SectionTitle>{label}</SectionTitle>
      {chain.map((v) => {
        const json = docs.byId.get(v.id)?.json;
        const entries: string[] =
          kind === "tag-vocabulary"
            ? ((json as TagVocabularyDoc | undefined)?.definitions ?? []).map((d) => d.key)
            : ((json as CategoryTreeDoc | undefined)?.categories ?? []).map((c) => c.name);
        return (
          <div key={v.id} className="flex flex-wrap items-baseline gap-1 text-12">
            <span className="w-28 shrink-0 text-secondary">{v.domain ? `from ${v.domain}` : "global"}</span>
            {entries.length ? (
              entries.map((e) => (
                <span key={e} className="rounded-[4px] border border-default px-1.5 py-0.5 font-mono">
                  {e}
                </span>
              ))
            ) : (
              <span className="text-secondary">none</span>
            )}
          </div>
        );
      })}
    </section>
  );
}

export function TagVocabularyEditor({ scope }: Scope) {
  const services = useServices();
  const { store } = services;
  const index = useIndex();
  const { id, json, edit, flush, loading, create } = useScopedVocabulary("tag-vocabulary", scope);
  // Where the picker starts for a tag with no color (or a CSS name it cannot show): the built-in accent.
  const pickerStart = useMemo(() => builtInAccent(document, "light") ?? undefined, []);
  // The uses each tag has (GET /api/model/tags/usage), refreshed with the model.
  const usage = useQuery({
    queryKey: ["tag-usage", scope ?? "", index.dataUpdatedAt],
    queryFn: () => endpoints.getTagUsage(scope),
    placeholderData: keepPreviousData,
  });
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [confirm, setConfirm] = useState<{ tags: string[] } | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (loading || (id && !json)) return <Spinner />;
  const vocab = (json ?? { definitions: [] }) as unknown as TagVocabularyDoc;
  const definitions = vocab.definitions ?? [];
  const uses = new Map((usage.data?.tags ?? []).map((t) => [t.tag, t]));
  const declaredKeys = new Set(definitions.map((d) => d.key));
  const undeclared = (usage.data?.tags ?? []).filter((t) => !t.declared && !declaredKeys.has(t.tag) && t.uses > 0);
  const toggle = (key: string, on: boolean) => {
    const next = new Set(selected);
    if (on) next.add(key);
    else next.delete(key);
    setSelected(next);
  };
  const declare = async (keys: string[]) => {
    if (!keys.length) return;
    if (!id) await create(keys.map((key) => ({ key })));
    else {
      edit((j) => void ((j as unknown as TagVocabularyDoc).definitions = [...definitions, ...keys.map((key) => ({ key }))]));
      await flush();
    }
    setSelected(new Set([...selected].filter((k) => !keys.includes(k))));
  };
  // The first tag of a scope with no vocabulary yet also declares the tags already in use there, so none turns into a note.
  const add = () => {
    const taken = new Set([...declaredKeys, ...undeclared.map((t) => t.tag)]);
    let n = definitions.length + 1;
    while (taken.has(`tag${n}`)) n++;
    const key = `tag${n}`;
    if (!id) {
      void create([{ key }, ...undeclared.map((t) => ({ key: t.tag }))]);
      if (undeclared.length) store.getState().notify(`Also declared the ${undeclared.length} ${undeclared.length === 1 ? "tag" : "tags"} already in use.`);
    } else edit((j) => void ((j as unknown as TagVocabularyDoc).definitions = [...definitions, { key }]));
  };
  const idsOf = (tags: readonly string[]) => [
    ...tags.flatMap((t) => uses.get(t)?.elements ?? []),
    ...(id && tags.some((t) => declaredKeys.has(t)) ? [id] : []),
  ];
  const retag = async (tags: string[], name?: string) => {
    setBusy(true);
    const label = name ? `Rename tag ${tags[0]} to ${name}` : tags.length === 1 ? `Remove tag ${tags[0]} everywhere` : `Remove ${tags.length} tags everywhere`;
    const failed = await commitRetag(services, label, { tags, ...(name ? { name } : {}), ...(scope ? { package: scope } : {}) }, idsOf(tags));
    setBusy(false);
    if (failed) store.getState().notify(failed, "error");
    else {
      setSelected(new Set([...selected].filter((k) => !tags.includes(k))));
      store
        .getState()
        .notify(name ? `Renamed ${tags[0]} to ${name} everywhere.` : `Removed ${tags.length === 1 ? tags[0] : `${tags.length} tags`} everywhere.`);
    }
  };
  // A tag nothing uses goes from the vocabulary alone; one in use asks first, with its counts.
  const remove = (key: string, i: number) => {
    if ((uses.get(key)?.uses ?? 0) > 0) setConfirm({ tags: [key] });
    else {
      edit((j) => void ((j as unknown as TagVocabularyDoc).definitions = definitions.filter((_, k) => k !== i)));
      void flush();
    }
  };
  const usedText = (key: string) => {
    const u = uses.get(key);
    if (!u) return usage.isPending ? "…" : "unused";
    if (!u.uses) return "unused";
    return `${u.uses} ${u.uses === 1 ? "use" : "uses"} · ${u.elements.length} ${u.elements.length === 1 ? "element" : "elements"}`;
  };
  const usedTitle = (key: string) => {
    const u = uses.get(key);
    return u?.examples.length
      ? `Used by ${u.examples.join(", ")}${u.elements.length > u.examples.length ? ` and ${u.elements.length - u.examples.length} more` : ""}`
      : undefined;
  };
  const selection = [...selected];
  return (
    <section className="flex max-w-4xl flex-col gap-2" aria-label={KIND_LABELS["tag-vocabulary"]} data-testid="tag-vocabulary-editor">
      {id ? (
        <CheckboxField
          id={`tags-strict-${scope ?? "global"}`}
          label="Strict: an undeclared tag is an error (MQ2006)"
          checked={vocab.strict === true}
          onChange={(v) => {
            edit((j) => setOptional(j as never, "strict", v ? true : undefined));
            void flush();
          }}
        />
      ) : null}
      <SectionTitle
        actions={
          <Button size="sm" onClick={add} data-testid="tags-add">
            <Plus /> Add tag
          </Button>
        }
      >
        {scope ? "This domain's tags" : "Tags"}
      </SectionTitle>
      {selection.length ? (
        <div className="flex flex-wrap items-center gap-2 rounded-control border border-default bg-app px-2 py-1 text-12" data-testid="tags-selection">
          <span>{selection.length} selected</span>
          <Button size="sm" variant="danger" onClick={() => setConfirm({ tags: selection })} disabled={busy} data-testid="tags-remove-selected">
            <Trash2 /> Remove everywhere…
          </Button>
          {selection.some((k) => !declaredKeys.has(k)) ? (
            <Button size="sm" onClick={() => void declare(selection.filter((k) => !declaredKeys.has(k)))} disabled={busy} data-testid="tags-declare-selected">
              <Plus /> Add to the vocabulary
            </Button>
          ) : null}
          <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
            Clear the selection
          </Button>
        </div>
      ) : null}
      {!definitions.length ? (
        <p className="text-12 text-secondary">
          {scope
            ? "No tags of its own: the domain's vocabulary is created with the first tag you add."
            : "No tags yet: the vocabulary is created with the first tag, and the tags already in use are declared with it."}
        </p>
      ) : (
        <table className="w-full text-13" aria-label="Tags">
          <thead>
            <tr className="text-left text-11 text-secondary">
              <th className="w-6">
                <span className="sr-only">Select</span>
              </th>
              <th className="font-semibold">Key</th>
              <th className="font-semibold">Description</th>
              <th className="font-semibold">Color</th>
              <th className="font-semibold">Used</th>
              <th>
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {definitions.map((d, i) => {
              const used = (uses.get(d.key)?.uses ?? 0) > 0;
              return (
                <tr key={i}>
                  <td className="py-1">
                    <input type="checkbox" aria-label={`Select tag ${d.key}`} checked={selected.has(d.key)} onChange={(e) => toggle(d.key, e.target.checked)} />
                  </td>
                  <td className="py-1 pr-2">
                    <Input
                      aria-label={`Key of tag ${i + 1}`}
                      className="font-mono"
                      value={d.key}
                      readOnly={used}
                      title={used ? "In use: rename it everywhere with the rename button" : undefined}
                      onChange={(e) => edit((j) => void ((j as unknown as TagVocabularyDoc).definitions![i].key = e.target.value))}
                      onBlur={() => void flush()}
                    />
                  </td>
                  <td className="py-1 pr-2">
                    <Input
                      aria-label={`Description of ${d.key}`}
                      value={d.description ?? ""}
                      onChange={(e) => edit((j) => setOptional((j as unknown as TagVocabularyDoc).definitions![i] as never, "description", e.target.value))}
                      onBlur={() => void flush()}
                    />
                  </td>
                  <td className="py-1 pr-2">
                    <div className="flex items-center gap-2">
                      {/* The picker writes a hex color; the field beside it still takes any CSS color. */}
                      <input
                        type="color"
                        aria-label={`Pick the color of ${d.key}`}
                        title={`Pick the color of ${d.key}`}
                        className="h-6 w-8 shrink-0 cursor-pointer rounded-control border border-input bg-surface p-0"
                        value={normalizeHexColor(d.color) ?? pickerStart}
                        onChange={(e) => edit((j) => setOptional((j as unknown as TagVocabularyDoc).definitions![i] as never, "color", e.target.value))}
                        onBlur={() => void flush()}
                        data-testid={`tag-color-picker-${i + 1}`}
                      />
                      <Input
                        aria-label={`Color of ${d.key}`}
                        className="w-28 font-mono"
                        value={d.color ?? ""}
                        onChange={(e) => edit((j) => setOptional((j as unknown as TagVocabularyDoc).definitions![i] as never, "color", e.target.value))}
                        onBlur={() => void flush()}
                      />
                    </div>
                  </td>
                  <td className="whitespace-nowrap py-1 pr-2 text-12 text-secondary" title={usedTitle(d.key)} data-testid={`tag-used-${d.key}`}>
                    {usedText(d.key)}
                  </td>
                  <td className="whitespace-nowrap">
                    <Button size="icon-sm" variant="ghost" label={`Rename ${d.key} everywhere`} onClick={() => setRenaming(d.key)} disabled={busy}>
                      <Pencil />
                    </Button>
                    <Button size="icon-sm" variant="ghost" label={`Remove tag ${d.key}`} onClick={() => remove(d.key, i)} disabled={busy}>
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {undeclared.length ? (
        <section className="flex flex-col gap-1" aria-label="Tags in use, not declared" data-testid="tags-undeclared">
          <SectionTitle
            actions={
              <Button size="sm" onClick={() => void declare(undeclared.map((t) => t.tag))} disabled={busy} data-testid="tags-declare-all">
                <Plus /> Add all {undeclared.length}
              </Button>
            }
          >
            Tags in use, not declared
          </SectionTitle>
          <p className="text-12 text-secondary">
            Used {scope ? "in this domain" : "in the model"} but declared in no vocabulary on the element&apos;s domain chain: each is a note (MQ2006)
            {vocab.strict ? ", an error while the vocabulary is strict" : ""}. Add them, or remove them everywhere.
          </p>
          <ul className="flex flex-col">
            {undeclared.map((t) => (
              <li key={t.tag} className="flex h-7 items-center gap-2 text-13" data-testid={`tag-undeclared-${t.tag}`}>
                <input type="checkbox" aria-label={`Select tag ${t.tag}`} checked={selected.has(t.tag)} onChange={(e) => toggle(t.tag, e.target.checked)} />
                <span className="w-48 truncate font-mono">{t.tag}</span>
                <span className="text-12 text-secondary" title={usedTitle(t.tag)}>
                  {usedText(t.tag)}
                </span>
                <span className="flex-1" />
                <Button size="sm" variant="ghost" onClick={() => void declare([t.tag])} disabled={busy}>
                  Add
                </Button>
                <Button size="icon-sm" variant="ghost" label={`Remove tag ${t.tag} everywhere`} onClick={() => setConfirm({ tags: [t.tag] })} disabled={busy}>
                  <Trash2 />
                </Button>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      {scope ? <Inherited kind="tag-vocabulary" scope={scope} /> : null}
      {confirm ? (
        <RemoveTagsDialog
          tags={confirm.tags.map((t) => ({ tag: t, text: usedText(t), declared: declaredKeys.has(t) }))}
          busy={busy}
          onClose={() => setConfirm(null)}
          onRemove={async () => {
            await retag(confirm.tags);
            setConfirm(null);
          }}
        />
      ) : null}
      {renaming !== null ? (
        <RenameTagDialog
          tag={renaming}
          text={usedText(renaming)}
          taken={declaredKeys}
          busy={busy}
          onClose={() => setRenaming(null)}
          onRename={async (name) => {
            await retag([renaming], name);
            setRenaming(null);
          }}
        />
      ) : null}
    </section>
  );
}

/** Asks before tags go from the vocabulary and from every use, with each one's counts; one undo step. */
function RemoveTagsDialog({
  tags,
  busy,
  onClose,
  onRemove,
}: {
  tags: { tag: string; text: string; declared: boolean }[];
  busy: boolean;
  onClose: () => void;
  onRemove: () => Promise<void>;
}) {
  const title = tags.length === 1 ? `Remove ${tags[0].tag} everywhere?` : `Remove ${tags.length} tags everywhere?`;
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent title={title} description="From the vocabulary and from every element and sub-element that uses them here. One undo step.">
        <ul className="flex max-h-[40vh] flex-col overflow-auto text-13" data-testid="remove-tags-list">
          {tags.map((t) => (
            <li key={t.tag} className="flex gap-2">
              <span className="font-mono">{t.tag}</span>
              <span className="text-secondary">
                {t.text}
                {t.declared ? "" : ", not declared"}
              </span>
            </li>
          ))}
        </ul>
        <div className="flex justify-end gap-2 pt-2">
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="danger" onClick={() => void onRemove()} disabled={busy} data-testid="remove-tags-apply">
            {busy ? "Removing…" : "Remove everywhere"}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

/** A new key for a tag, in the vocabulary and in every use; a key the vocabulary has already merges into it. */
function RenameTagDialog({
  tag,
  text,
  taken,
  busy,
  onClose,
  onRename,
}: {
  tag: string;
  text: string;
  taken: ReadonlySet<string>;
  busy: boolean;
  onClose: () => void;
  onRename: (name: string) => Promise<void>;
}) {
  const [name, setName] = useState(tag);
  const key = name.trim();
  const problem = !key || key === tag || isTagKey(key) ? null : "A tag key is 1 to 64 characters without spaces.";
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent title={`Rename ${tag} everywhere`} description={`The vocabulary's entry and every use (${text}). One undo step.`}>
        <form
          className="flex flex-col gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (key && key !== tag && !problem) void onRename(key);
          }}
        >
          <Input aria-label="New key" className="font-mono" value={name} autoFocus onChange={(e) => setName(e.target.value)} />
          {problem ? <p className="text-12 text-danger">{problem}</p> : null}
          {key !== tag && taken.has(key) ? (
            <p className="text-12 text-secondary" role="status">
              {key} is declared already: the uses of {tag} merge into it and its entry goes.
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={busy || !key || key === tag || !!problem} data-testid="rename-tag-apply">
              {busy ? "Renaming…" : "Rename everywhere"}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function CategoryTreeEditor({ scope }: Scope) {
  const { id, json, edit, flush, loading, create } = useScopedVocabulary("category-tree", scope);
  if (loading || (id && !json)) return <Spinner />;
  const tree = (json ?? { categories: [] }) as unknown as CategoryTreeDoc;
  const categories = tree.categories ?? [];
  const children = (parent: string | null) => categories.filter((c) => (c.parent ?? null) === parent).sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
  const mutate = (fn: (list: CategoryDoc[]) => void) => {
    edit((j) => {
      const t = j as unknown as CategoryTreeDoc;
      const list = t.categories ?? [];
      fn(list);
      t.categories = list;
    });
    void flush();
  };
  const move = (c: CategoryDoc, delta: -1 | 1) =>
    mutate((list) => {
      const siblings = list.filter((x) => (x.parent ?? null) === (c.parent ?? null)).sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
      const at = siblings.findIndex((x) => x.id === c.id);
      const other = siblings[at + delta];
      if (!other) return;
      siblings.forEach((s, i) => (s.order = i + 1));
      const a = list.find((x) => x.id === c.id)!;
      const b = list.find((x) => x.id === other.id)!;
      [a.order, b.order] = [b.order, a.order];
    });
  const addTop = () => {
    const first = { id: newId(), name: "New category", order: children(null).length + 1 };
    if (!id) void create(first);
    else mutate((list) => void list.push(first));
  };
  const render = (parent: string | null, depth: number) => (
    <ul className="flex flex-col gap-1" role={depth === 0 ? "tree" : "group"} aria-label={depth === 0 ? "Categories" : undefined}>
      {children(parent).map((c) => (
        <li key={c.id} role="treeitem" aria-level={depth + 1} aria-selected={false}>
          <div className="flex items-center gap-1" style={{ paddingLeft: depth * 20 }}>
            <Input
              aria-label={`Name of category ${c.name}`}
              className="h-7 w-56"
              value={c.name}
              onChange={(e) =>
                edit((j) => {
                  const cat = (j as unknown as CategoryTreeDoc).categories!.find((x) => x.id === c.id);
                  if (cat) cat.name = e.target.value;
                })
              }
              onBlur={() => void flush()}
            />
            <Button size="icon-sm" variant="ghost" label={`Move ${c.name} up`} onClick={() => move(c, -1)}>
              <ArrowUp />
            </Button>
            <Button size="icon-sm" variant="ghost" label={`Move ${c.name} down`} onClick={() => move(c, 1)}>
              <ArrowDown />
            </Button>
            <Button
              size="icon-sm"
              variant="ghost"
              label={`Add a category under ${c.name}`}
              onClick={() => mutate((list) => void list.push({ id: newId(), name: "New category", parent: c.id, order: children(c.id).length + 1 }))}
            >
              <Plus />
            </Button>
            <Button
              size="icon-sm"
              variant="ghost"
              label={`Remove ${c.name}`}
              disabled={children(c.id).length > 0}
              onClick={() =>
                mutate(
                  (list) =>
                    void list.splice(
                      list.findIndex((x) => x.id === c.id),
                      1,
                    ),
                )
              }
            >
              <Trash2 />
            </Button>
          </div>
          {children(c.id).length ? render(c.id, depth + 1) : null}
        </li>
      ))}
    </ul>
  );
  return (
    <section className="flex max-w-3xl flex-col gap-2" aria-label={KIND_LABELS["category-tree"]} data-testid="category-tree-editor">
      <SectionTitle
        actions={
          <Button size="sm" onClick={addTop}>
            <Plus /> Add category
          </Button>
        }
      >
        {scope ? "This domain's categories" : "Categories"}
      </SectionTitle>
      {categories.length ? (
        render(null, 0)
      ) : (
        <p className="text-12 text-secondary">
          {scope
            ? "No categories of its own: the domain's tree is created with the first category you add."
            : "No categories yet: the tree is created with the first category."}
        </p>
      )}
      <p className="text-12 text-secondary">
        Categories color entity cards (in tree order) and group the explorer's category filter. A category still used by an element cannot be removed without a
        validation error (MQ2005).
      </p>
      {scope ? <Inherited kind="category-tree" scope={scope} /> : null}
    </section>
  );
}
