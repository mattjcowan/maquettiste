// What the storage run says about one relation in one database (several-types-design.md 19.10): the types left out, the placed types no key or
// column points at, and what the database can't enforce, each with the text to copy. It reads the database view's `relationStorage`.
import { Copy } from "lucide-react";
import { useState } from "react";
import { useDatabaseView } from "@/api/queries";
import type { components } from "@/api/schema";
import { Button } from "@/components/ui/button";
import { SectionTitle } from "@/components/ui/misc";

export type RelationStorageView = components["schemas"]["RelationStorageView"];
export type GapKind = components["schemas"]["RelationStorageGapView"]["kind"];

/** One gap in plain words. */
export const GAP_WORDS: Record<GapKind, string> = {
  "no-foreign-key": "No foreign key: the database can't refuse a link to a missing row, or apply the delete rule.",
  "shared-table": "A key to a shared table: the database can't refuse a row of a type this end doesn't cover.",
  "id-only": "An id alone: the database can't check the type the id points at.",
  "type-check": "No check on the type codes.",
  "count-check": "No check on how many of the keys are set.",
  "one-to-one-unique": "No unique on the pointer, so two rows can link to one object.",
  "link-unique": "No unique over both pointers, so a link can be stored twice.",
  "per-type-duplicates": "Keys per type in a link table: a unique over nullable columns doesn't stop duplicate links in most databases.",
  "rows-not-told-apart": "A table holds rows of other entities, and nothing tells them apart.",
};

export interface StorageLists {
  leftOut: string | null;
  notStored: string | null;
  gaps: { endId: string; role: string; kind: GapKind; words: string; text: string | null }[];
}

const names = (list: { entityId: string }[], nameOf: (id: string) => string) => [...new Set(list.map((x) => nameOf(x.entityId)))].join(", ");

/** The lines the Storage tab shows for a relation in a database (pure: the view, then names for ids). */
export function storageLists(item: RelationStorageView | undefined, nameOf: (id: string) => string, roleOf: (endId: string) => string): StorageLists {
  if (!item) return { leftOut: null, notStored: null, gaps: [] };
  return {
    leftOut: item.leftOut.length ? `Left out (not in this database): ${names(item.leftOut, nameOf)}` : null,
    notStored: item.notStored.length ? `Not stored: ${names(item.notStored, nameOf)}, no key or column points at it` : null,
    gaps: item.gaps.map((g) => ({ endId: g.endId, role: roleOf(g.endId), kind: g.kind, words: GAP_WORDS[g.kind], text: g.text ?? null })),
  };
}

/** The lists for a relation in one database, read from that database's view (nothing while the view is out or has no item for it). */
export function DatabaseRunLists({
  databaseId,
  relationId,
  nameOf,
  roleOf,
}: {
  databaseId: string;
  relationId: string;
  nameOf: (id: string) => string;
  roleOf: (endId: string) => string;
}) {
  const view = useDatabaseView(databaseId).data?.view;
  const item = view?.relationStorage?.find((x) => x.relationId === relationId);
  return <StorageRunLists item={item} nameOf={nameOf} roleOf={roleOf} />;
}

export function StorageRunLists({
  item,
  nameOf,
  roleOf,
}: {
  item: RelationStorageView | undefined;
  nameOf: (id: string) => string;
  roleOf: (endId: string) => string;
}) {
  const lists = storageLists(item, nameOf, roleOf);
  const [copied, setCopied] = useState<number | null>(null);
  if (!lists.leftOut && !lists.notStored && !lists.gaps.length) return null;
  return (
    <div className="flex flex-col gap-1" data-testid="relation-storage-run">
      {lists.leftOut ? (
        <p className="text-12 text-secondary" data-testid="relation-storage-left-out">
          {lists.leftOut}
        </p>
      ) : null}
      {lists.notStored ? (
        <p className="text-12 text-warning" data-testid="relation-storage-not-stored">
          {lists.notStored}
        </p>
      ) : null}
      {lists.gaps.length ? (
        <div className="flex flex-col gap-1" data-testid="relation-storage-gaps">
          <SectionTitle>What the database can't enforce</SectionTitle>
          <ul className="flex flex-col gap-1 text-12">
            {lists.gaps.map((g, i) => (
              <li key={i} className="flex flex-col gap-0.5" data-testid="relation-storage-gap" data-kind={g.kind}>
                <span>
                  <span className="font-medium">{g.role}</span>: {g.words}
                </span>
                {g.text ? (
                  <span className="flex items-start gap-1">
                    <code className="min-w-0 flex-1 break-all rounded-sm bg-surface-2 px-1 py-0.5 font-mono text-11" data-testid="relation-storage-gap-text">
                      {g.text}
                    </code>
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      label={copied === i ? "Copied" : "Copy the text"}
                      data-testid="relation-storage-gap-copy"
                      onClick={() => {
                        void navigator.clipboard?.writeText(g.text!).catch(() => undefined);
                        setCopied(i);
                      }}
                    >
                      <Copy className="size-3.5" />
                    </Button>
                  </span>
                ) : null}
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
}
