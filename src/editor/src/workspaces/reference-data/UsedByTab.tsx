// The Used by tab (reference-types-seeds-localization.md 4.3): every attribute typed by the reference type, grouped
// by owner kind then domain, with collection and required badges; a row goes to its owner.
import { useIndex, useElements } from "@/api/queries";
import type { AttributeDoc, ReferenceTypeUsage } from "@/api/types";
import { useEditorNavigation } from "@/app/navigation";
import { Badge, EmptyState, Spinner } from "@/components/ui/misc";
import { GROUP_LABELS, KIND_LABELS } from "@/model/labels";

export function UsedByTab({ usage, loading }: { usage: ReferenceTypeUsage | undefined; loading: boolean }) {
  const index = useIndex();
  const { goTo } = useEditorNavigation();
  const usages = usage?.usages ?? [];
  const owners = [...new Set(usages.map((u) => u.owner))];
  const docs = useElements(owners);
  if (loading) return <Spinner />;
  if (!usages.length)
    return (
      <EmptyState title="Not used yet">
        <p className="text-12 text-secondary">No attribute has this reference type as its type. Pick it in an attribute&apos;s Type cell.</p>
      </EmptyState>
    );
  const summary = (id: string | null | undefined) => (id ? index.data?.find((r) => r.id === id) : undefined);
  const groups = new Map<string, typeof usages>();
  for (const u of usages) {
    const owner = summary(u.owner);
    const key = `${owner ? KIND_LABELS[owner.kind] : "?"} · ${summary(u.domain)?.name ?? GROUP_LABELS.notInDomain}`;
    groups.set(key, [...(groups.get(key) ?? []), u]);
  }
  return (
    <div className="flex max-w-3xl flex-col gap-3" data-testid="reference-used-by">
      {[...groups.entries()]
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([group, list]) => (
          <section key={group} aria-label={group}>
            <h3 className="mb-1 text-11 font-semibold uppercase tracking-wide text-secondary">{group}</h3>
            <ul className="flex flex-col rounded-control border border-default">
              {list.map((u) => {
                const attributes = ((docs.byId.get(u.owner)?.json as { attributes?: AttributeDoc[] } | undefined)?.attributes ?? []) as AttributeDoc[];
                const name = attributes.find((a) => a.id === u.attribute)?.name ?? u.attribute;
                return (
                  <li key={u.attribute} className="border-t border-default first:border-t-0">
                    <button
                      type="button"
                      className="flex h-8 w-full items-center gap-2 px-2 text-left text-13 hover:bg-accent-subtle"
                      onClick={() => goTo(u.owner)}
                    >
                      <span className="min-w-0 flex-1 truncate">
                        {summary(u.owner)?.name ?? u.owner}.<span className="font-mono">{name}</span>
                      </span>
                      {u.collection ? <Badge>many</Badge> : null}
                      {u.required ? <Badge>required</Badge> : null}
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>
        ))}
    </div>
  );
}
