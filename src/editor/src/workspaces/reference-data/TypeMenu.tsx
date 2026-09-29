// The reference type menu and its dialogs (reference-types-seeds-localization.md 4.2): Duplicate, Rename, Move to
// category, Set storage, Export CSV, Convert to enum and Delete with the usage check of section 2.7. Opened by a right
// click in the screen's type list, or by the Reference data explorer's row menu through the store (`typeAction`).
// Every write is one batch that undo reverses.
import { Fragment, useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { applyBatchResult, elementQuery, useIndex } from "@/api/queries";
import { indexLookup } from "@/model/index";
import * as endpoints from "@/api/endpoints";
import type { ElementDocument, ModelJson, ReferenceTypeDoc, ReferenceTypeUsage, SeedDoc } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/menu";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { IDENTIFIER } from "@/model/model";
import { useEditor } from "@/state/store";
import { exportCsv } from "./actions";
import type { CategoryInfo, RefTypeItem } from "./listModel";
import { copyName, duplicateType, enumConversionProblem, enumFromType, isTypeAction, retypeAttributes, typeMenuFor, type TypeActionId } from "./typeMenu";

export interface TypeMenuAt {
  ids: string[];
  x: number;
  y: number;
}

type Op =
  | { op: "create"; element: ModelJson }
  | { op: "update"; id: string; expectedHash: string; element: ModelJson }
  | { op: "delete"; id: string; expectedHash: string };

type Pending =
  | { action: "rename"; item: RefTypeItem }
  | { action: "move-category"; items: RefTypeItem[] }
  | { action: "convert-to-enum"; item: RefTypeItem; problem: string | null }
  | { action: "delete"; items: RefTypeItem[]; usages: ReferenceTypeUsage["usages"] | null };

export function TypeMenu({
  items,
  categories,
  menu,
  onClose,
  onSelect,
  onStorage,
}: {
  items: readonly RefTypeItem[];
  categories: readonly CategoryInfo[];
  menu: TypeMenuAt | null;
  onClose(): void;
  onSelect(id: string): void;
  onStorage(id: string): void;
}) {
  const services = useServices();
  const { store, drafts } = services;
  const qc = useQueryClient();
  const [pending, setPending] = useState<Pending | null>(null);
  const lookup = indexLookup(useIndex().data);
  const targets = (ids: readonly string[]) => ids.map((id) => items.find((i) => i.id === id)).filter((i): i is RefTypeItem => !!i);
  const menuTargets = menu ? targets(menu.ids) : [];
  const notify = (text: string, level?: "error") => store.getState().notify(text, level);

  const load = async (id: string): Promise<ElementDocument> => {
    await drafts.flush(id);
    return qc.fetchQuery({ ...elementQuery(id), staleTime: 0 });
  };

  /** One batch; on success the cache takes the result and undo gets one entry. */
  const commit = async (label: string, ops: Op[], before: (ModelJson | null)[]): Promise<boolean> => {
    const result = await endpoints.applyBatch({ operations: ops as never });
    if (!endpoints.isBatchResult(result)) {
      notify(`${label} failed: ${result.diagnostics[0]?.message ?? "the batch did not parse"}`, "error");
      return false;
    }
    if (result.outcome !== "saved") {
      const failed = result.items.find((i) => i.outcome !== "saved");
      notify(`${label} failed: ${failed?.diagnostics[0]?.message ?? result.outcome}`, "error");
      return false;
    }
    applyBatchResult(qc, result);
    store.getState().pushUndo({
      label,
      ids: ops.map((o) => ("id" in o ? o.id : String((o.element as { id: string }).id))),
      before: before.map((b) => (b ? clone(b) : null)),
      after: ops.map((o) => (o.op === "delete" ? null : clone(o.element))),
      afterHashes: ops.map((o, i) => (o.op === "delete" ? null : (result.items[i]?.hash ?? null))),
    });
    return true;
  };

  const run = async (action: TypeActionId, ids: string[]) => {
    const chosen = targets(ids);
    const first = chosen[0];
    if (!first) return;
    switch (action) {
      case "duplicate": {
        const type = await load(first.id);
        const seeds = await Promise.all(first.seeds.map(load));
        const name = copyName(
          first.name,
          items.map((i) => i.name),
        );
        const copy = duplicateType(
          type.json as ReferenceTypeDoc,
          seeds.map((s) => s.json as SeedDoc),
          name,
          newId,
        );
        const ops: Op[] = [copy.type, ...copy.seeds].map((element) => ({ op: "create", element: element as unknown as ModelJson }));
        if (
          await commit(
            `Duplicate ${first.name}`,
            ops,
            ops.map(() => null),
          )
        ) {
          onSelect(copy.type.id);
          notify(`Created ${name}.`);
        }
        return;
      }
      case "rename":
        setPending({ action: "rename", item: first });
        return;
      case "move-category":
        setPending({ action: "move-category", items: chosen });
        return;
      case "set-storage":
        onSelect(first.id);
        onStorage(first.id);
        return;
      case "export-csv": {
        const seed = first.seeds.map((s) => ({ id: s, name: first.name }))[0];
        if (seed) await exportCsv(seed);
        return;
      }
      case "convert-to-enum": {
        const type = await load(first.id);
        const seeds = await Promise.all(first.seeds.map(load));
        setPending({
          action: "convert-to-enum",
          item: first,
          problem: enumConversionProblem(
            type.json as ReferenceTypeDoc,
            seeds.map((s) => s.json as SeedDoc),
          ),
        });
        return;
      }
      case "delete": {
        setPending({ action: "delete", items: chosen, usages: null });
        const answers = await Promise.all(chosen.map((c) => endpoints.getReferenceTypeUsage(c.id)));
        setPending({ action: "delete", items: chosen, usages: answers.flatMap((a) => a.usages) });
        return;
      }
    }
  };

  // The explorer's row menu asks through the store; the request is taken once.
  const request = useEditor(store, (s) => s.typeAction);
  useEffect(() => {
    if (!request || !isTypeAction(request.action) || !items.length) return;
    store.getState().requestTypeAction(null);
    void run(request.action, request.ids);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs once per request
  }, [request, items.length]);

  const rename = async (item: RefTypeItem, name: string) => {
    const type = await load(item.id);
    const seeds = await Promise.all(item.seeds.map(load));
    const ops: Op[] = [];
    const before: ModelJson[] = [];
    const renamed = { ...(type.json as Record<string, unknown>), name } as unknown as ModelJson;
    ops.push({ op: "update", id: item.id, expectedHash: type.hash, element: renamed });
    before.push(type.json as ModelJson);
    // The seed named after the type (Import and Export use it) follows the new name.
    for (const s of seeds)
      if ((s.json as SeedDoc).name === item.name) {
        ops.push({
          op: "update",
          id: (s.json as SeedDoc).id,
          expectedHash: s.hash,
          element: { ...(s.json as Record<string, unknown>), name } as unknown as ModelJson,
        });
        before.push(s.json as ModelJson);
      }
    if (await commit(`Rename ${item.name}`, ops, before)) setPending(null);
  };

  const moveToCategory = async (chosen: RefTypeItem[], category: string | null) => {
    const docs = await Promise.all(chosen.map((c) => load(c.id)));
    const ops: Op[] = docs.map((d) => {
      const json = { ...(d.json as Record<string, unknown>) };
      if (category) json.category = category;
      else delete json.category;
      return { op: "update", id: String(json.id), expectedHash: d.hash, element: json as unknown as ModelJson };
    });
    if (
      await commit(
        chosen.length === 1 ? `Move ${chosen[0].name}` : `Move ${chosen.length} types`,
        ops,
        docs.map((d) => d.json as ModelJson),
      )
    )
      setPending(null);
  };

  const convert = async (item: RefTypeItem) => {
    const type = await load(item.id);
    const seeds = await Promise.all(item.seeds.map(load));
    const usage = await endpoints.getReferenceTypeUsage(item.id);
    const owners = await Promise.all([...new Set(usage.usages.map((u) => u.owner))].filter((o) => o !== item.id).map(load));
    const made = enumFromType(
      type.json as ReferenceTypeDoc,
      seeds.map((s) => s.json as SeedDoc),
      newId,
    );
    const ops: Op[] = [{ op: "create", element: made as unknown as ModelJson }];
    const before: (ModelJson | null)[] = [null];
    for (const o of owners) {
      const json = structuredClone(o.json) as unknown as Record<string, unknown>;
      if (!retypeAttributes(json, item.id, made.id)) continue;
      ops.push({ op: "update", id: String(json.id), expectedHash: o.hash, element: json as unknown as ModelJson });
      before.push(o.json as ModelJson);
    }
    for (const s of seeds) {
      ops.push({ op: "delete", id: String((s.json as SeedDoc).id), expectedHash: s.hash });
      before.push(s.json as ModelJson);
    }
    ops.push({ op: "delete", id: item.id, expectedHash: type.hash });
    before.push(type.json as ModelJson);
    if (await commit(`Convert ${item.name} to an enum`, ops, before)) {
      setPending(null);
      notify(`${item.name} is now an enum with ${made.members?.length ?? 0} members.`);
    }
  };

  const remove = async (chosen: RefTypeItem[]) => {
    const ops: Op[] = [];
    const before: ModelJson[] = [];
    for (const c of chosen) {
      // The type's own seeds are not users: they go with it (RT 2.7).
      for (const s of await Promise.all(c.seeds.map(load))) {
        ops.push({ op: "delete", id: String((s.json as SeedDoc).id), expectedHash: s.hash });
        before.push(s.json as ModelJson);
      }
      const type = await load(c.id);
      ops.push({ op: "delete", id: c.id, expectedHash: type.hash });
      before.push(type.json as ModelJson);
    }
    if (await commit(chosen.length === 1 ? `Delete ${chosen[0].name}` : `Delete ${chosen.length} types`, ops, before)) {
      setPending(null);
      const s = store.getState();
      s.select(s.selection.filter((x) => !chosen.some((c) => c.id === x)));
    }
  };

  return (
    <>
      <DropdownMenu open={!!menu} onOpenChange={(open) => !open && onClose()} modal={false}>
        <DropdownMenuTrigger asChild>
          <span aria-hidden className="pointer-events-none fixed size-0" style={{ left: menu?.x ?? 0, top: menu?.y ?? 0 }} />
        </DropdownMenuTrigger>
        {menu ? (
          <DropdownMenuContent align="start" aria-label="Reference type actions" data-testid="type-menu">
            <DropdownMenuLabel>{menuTargets.length === 1 ? menuTargets[0].name : `${menuTargets.length} types`}</DropdownMenuLabel>
            {typeMenuFor(menuTargets).map((i, n) => (
              <Fragment key={i.id}>
                {i.danger && n > 0 ? <DropdownMenuSeparator /> : null}
                <DropdownMenuItem
                  className={i.danger ? "text-danger" : undefined}
                  onSelect={() => {
                    const ids = menu.ids;
                    onClose();
                    void run(i.id, ids);
                  }}
                >
                  {i.label}
                </DropdownMenuItem>
              </Fragment>
            ))}
          </DropdownMenuContent>
        ) : null}
      </DropdownMenu>
      {pending?.action === "rename" ? (
        <RenameDialog
          key={pending.item.id}
          item={pending.item}
          taken={items.map((i) => i.name)}
          onClose={() => setPending(null)}
          onRename={(n) => void rename(pending.item, n)}
        />
      ) : null}
      {pending?.action === "move-category" ? (
        <CategoryDialog items={pending.items} categories={categories} onClose={() => setPending(null)} onMove={(c) => void moveToCategory(pending.items, c)} />
      ) : null}
      {pending?.action === "convert-to-enum" ? (
        <Dialog open onOpenChange={(open) => !open && setPending(null)}>
          <DialogContent
            title={`Convert ${pending.item.name} to an enum`}
            description="Each row becomes a member named after its code, with its label as display name; the fields that use the type use the enum; the type and its seeds are deleted. Undo reverses it."
          >
            <div className="flex flex-col gap-3" data-testid="convert-to-enum-dialog">
              {pending.problem ? (
                <p role="alert" className="text-13 text-danger">
                  {pending.problem}
                </p>
              ) : (
                <p className="text-13">
                  {pending.item.rows} {pending.item.rows === 1 ? "row becomes a member" : "rows become members"}.
                </p>
              )}
              <div className="flex justify-end gap-2">
                <Button onClick={() => setPending(null)}>Cancel</Button>
                <Button variant="primary" disabled={!!pending.problem} onClick={() => void convert(pending.item)}>
                  Convert
                </Button>
              </div>
            </div>
          </DialogContent>
        </Dialog>
      ) : null}
      {pending?.action === "delete" ? (
        <Dialog open onOpenChange={(open) => !open && setPending(null)}>
          <DialogContent title={pending.items.length === 1 ? `Delete ${pending.items[0].name}` : `Delete ${pending.items.length} reference types`}>
            <div className="flex flex-col gap-3" data-testid="delete-type-dialog">
              {pending.usages === null ? (
                <p className="text-13 text-secondary">Looking for fields that use {pending.items.length === 1 ? "it" : "them"}…</p>
              ) : pending.usages.length ? (
                <>
                  <p role="alert" className="text-13 text-danger">
                    Not deleted: {pending.usages.length} {pending.usages.length === 1 ? "field uses" : "fields use"}{" "}
                    {pending.items.length === 1 ? "this type" : "these types"}. Change their type first.
                  </p>
                  <ul className="max-h-48 list-disc overflow-auto pl-5 text-13" data-testid="delete-type-usages">
                    {pending.usages.map((u) => (
                      <li key={`${u.owner}:${u.attribute}`}>{lookup.nameOf(u.owner) ?? u.owner}</li>
                    ))}
                  </ul>
                </>
              ) : (
                <p className="text-13">
                  {pending.items.map((i) => i.name).join(", ")} and {pending.items.reduce((n, i) => n + i.seeds.length, 0) === 1 ? "its seed" : "their seeds"} (
                  {pending.items.reduce((n, i) => n + i.rows, 0)} rows) will be deleted. Undo restores them.
                </p>
              )}
              <div className="flex justify-end gap-2">
                <Button onClick={() => setPending(null)}>{pending.usages?.length ? "Close" : "Cancel"}</Button>
                {pending.usages && !pending.usages.length ? (
                  <Button variant="danger" onClick={() => void remove(pending.items)} data-testid="confirm-delete-type">
                    Delete
                  </Button>
                ) : null}
              </div>
            </div>
          </DialogContent>
        </Dialog>
      ) : null}
    </>
  );
}

function RenameDialog({ item, taken, onClose, onRename }: { item: RefTypeItem; taken: readonly string[]; onClose(): void; onRename(name: string): void }) {
  const [name, setName] = useState(item.name);
  const clash = name !== item.name && taken.some((t) => t.toLowerCase() === name.toLowerCase());
  const problem = !IDENTIFIER.test(name) ? "A name is a letter or _ then letters, digits or _." : clash ? `${name} is taken.` : null;
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={`Rename ${item.name}`} description="The seed named after the type follows the new name.">
        <form
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (!problem && name !== item.name) onRename(name);
          }}
        >
          <Field label="Name" htmlFor="rename-type" hint={problem ?? undefined}>
            <Input id="rename-type" autoFocus value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!problem || undefined} />
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!!problem || name === item.name}>
              Rename
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function CategoryDialog({
  items,
  categories,
  onClose,
  onMove,
}: {
  items: readonly RefTypeItem[];
  categories: readonly CategoryInfo[];
  onClose(): void;
  onMove(category: string | null): void;
}) {
  const [category, setCategory] = useState(items.length === 1 ? (items[0].category ?? "") : "");
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={items.length === 1 ? `Move ${items[0].name} to a category` : `Move ${items.length} types to a category`}>
        <form
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            onMove(category || null);
          }}
        >
          <Field label="Category" htmlFor="type-category">
            <Select id="type-category" value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="">(none)</option>
              {categories.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.parent ? "— " : ""}
                  {c.label}
                </option>
              ))}
            </Select>
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary">
              Move
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
