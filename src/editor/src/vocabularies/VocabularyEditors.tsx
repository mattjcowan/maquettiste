// The tag vocabulary and category tree editors (explorer-redesign.md 1.11): the global ones in the Settings screen's
// Tags and Categories tabs, a domain's own in the domain editor's tabs. One of each kind per scope, created on first
// use (the first tag or category added). A domain's tab also lists, read-only, the vocabularies it inherits, nearest
// first ("from Billing", "global"). A key or name that a global or enclosing vocabulary already declares is MQ3021.
import { useMemo } from "react";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
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
import { ownVocabulary, vocabulariesOnChain, type VocabularyKind } from "@/model/vocabularies";

type Scope = { scope: string | null };

/** The scope's own vocabulary of a kind, as a draft, and a way to create it with its first entry. */
function useScopedVocabulary(kind: VocabularyKind, scope: string | null) {
  const index = useIndex();
  const { queryClient, store } = useServices();
  const rows = index.data;
  const id = useMemo(() => (rows ? (ownVocabulary(kind, scope, rows)?.id ?? null) : null), [rows, kind, scope]);
  const domainName = scope ? (rows?.find((r) => r.id === scope)?.name ?? "domain") : null;
  const create = async (first: Record<string, unknown>) => {
    const base = kind === "tag-vocabulary" ? "tags" : "categories";
    const name = domainName ? `${kebab(domainName)}-${base}` : base;
    const json = {
      kind,
      id: newId(),
      name,
      ...(scope ? { package: scope } : {}),
      ...(kind === "tag-vocabulary" ? { definitions: [first] } : { categories: [first] }),
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
  const { id, json, edit, flush, loading, create } = useScopedVocabulary("tag-vocabulary", scope);
  if (loading || (id && !json)) return <Spinner />;
  const vocab = (json ?? { definitions: [] }) as unknown as TagVocabularyDoc;
  const definitions = vocab.definitions ?? [];
  const add = () => {
    const key = `tag${definitions.length + 1}`;
    if (!id) void create({ key });
    else edit((j) => void ((j as unknown as TagVocabularyDoc).definitions = [...definitions, { key }]));
  };
  return (
    <section className="flex max-w-3xl flex-col gap-3" aria-label={KIND_LABELS["tag-vocabulary"]} data-testid="tag-vocabulary-editor">
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
          <Button size="sm" onClick={add}>
            <Plus /> Add tag
          </Button>
        }
      >
        {scope ? "This domain's tags" : "Tags"}
      </SectionTitle>
      {!definitions.length ? (
        <p className="text-12 text-secondary">
          {scope
            ? "No tags of its own: the domain's vocabulary is created with the first tag you add."
            : "No tags yet: the vocabulary is created with the first tag."}
        </p>
      ) : (
        <table className="w-full text-13" aria-label="Tags">
          <thead>
            <tr className="text-left text-11 text-secondary">
              <th className="font-semibold">Key</th>
              <th className="font-semibold">Description</th>
              <th className="font-semibold">Color</th>
              <th>
                <span className="sr-only">Remove</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {definitions.map((d, i) => (
              <tr key={i}>
                <td className="py-1 pr-2">
                  <Input
                    aria-label={`Key of tag ${i + 1}`}
                    className="font-mono"
                    value={d.key}
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
                    <span aria-hidden className="size-4 shrink-0 rounded-[4px] border border-default" style={{ background: d.color }} />
                    <Input
                      aria-label={`Color of ${d.key}`}
                      className="font-mono"
                      value={d.color ?? ""}
                      onChange={(e) => edit((j) => setOptional((j as unknown as TagVocabularyDoc).definitions![i] as never, "color", e.target.value))}
                      onBlur={() => void flush()}
                    />
                  </div>
                </td>
                <td>
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    aria-label={`Remove tag ${d.key}`}
                    onClick={() => {
                      edit((j) => void ((j as unknown as TagVocabularyDoc).definitions = definitions.filter((_, k) => k !== i)));
                      void flush();
                    }}
                  >
                    <Trash2 />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {scope ? <Inherited kind="tag-vocabulary" scope={scope} /> : null}
    </section>
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
            <Button size="icon-sm" variant="ghost" aria-label={`Move ${c.name} up`} onClick={() => move(c, -1)}>
              <ArrowUp />
            </Button>
            <Button size="icon-sm" variant="ghost" aria-label={`Move ${c.name} down`} onClick={() => move(c, 1)}>
              <ArrowDown />
            </Button>
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Add a category under ${c.name}`}
              onClick={() => mutate((list) => void list.push({ id: newId(), name: "New category", parent: c.id, order: children(c.id).length + 1 }))}
            >
              <Plus />
            </Button>
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Remove ${c.name}`}
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
    <section className="flex max-w-3xl flex-col gap-3" aria-label={KIND_LABELS["category-tree"]} data-testid="category-tree-editor">
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
