// The explorer's dialogs (explorer-redesign.md 1.8): Move to domain… (a domain's Open is the domain editor,
// editors/DomainEditor), the delete confirmation and Add with related… (canvas in step, step 10).
import { useEffect, useMemo, useRef, useState } from "react";
import { useElements } from "@/api/queries";
import type { DeletePlan, DeleteResolution, ElementSummary } from "@/api/types";
import { useServices } from "@/app/context";
import { DeletePlanDialog, runPlannedDelete } from "@/inspector/DeletePlanView";
import { requestDelete, useDeleteRequest } from "./deleteRequest";
import { marksLeavingScope } from "@/model/vocabularies";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { GROUP_LABELS } from "@/model/labels";
import { domainPath, type Forest } from "./tree";

/** Every domain as "Sales › Orders", sorted, except `exclude` and its sub-domains. */
export function domainChoices(forest: Forest, exclude: ReadonlySet<string> = new Set()): { id: string; path: string }[] {
  const out: { id: string; path: string }[] = [];
  for (const r of forest.byId.values()) {
    if (r.kind !== "package") continue;
    let skip = false;
    for (let d: string | null | undefined = r.id, guard = 0; d && guard < 64; d = forest.byId.get(d)?.package, guard++) if (exclude.has(d)) skip = true;
    if (!skip) out.push({ id: r.id, path: domainPath(forest, r.id) });
  }
  return out.sort((a, b) => a.path.localeCompare(b.path));
}
export function MoveDialog({
  forest,
  ids,
  onClose,
  onMove,
}: {
  forest: Forest;
  ids: readonly string[] | null;
  onClose: () => void;
  onMove: (domain: string | null) => void;
}) {
  const [target, setTarget] = useState("");
  const exclude = new Set((ids ?? []).filter((id) => forest.byId.get(id)?.kind === "package"));
  const rows = useMemo(() => [...forest.byId.values()] as unknown as ElementSummary[], [forest]);
  const vocabularies = useElements(ids ? rows.filter((r) => r.kind === "tag-vocabulary" || r.kind === "category-tree").map((r) => r.id) : []);
  const leaving = ids
    ? marksLeavingScope(
        ids.map((id) => forest.byId.get(id)).filter((r): r is NonNullable<typeof r> => !!r),
        target || null,
        rows,
        (id) => vocabularies.byId.get(id)?.json,
      )
    : [];
  const categoryName = (id: string) =>
    rows
      .filter((r) => r.kind === "category-tree")
      .flatMap((r) => (vocabularies.byId.get(r.id)?.json as { categories?: { id: string; name: string }[] } | undefined)?.categories ?? [])
      .find((c) => c.id === id)?.name ?? id;
  const choices = ids ? domainChoices(forest, exclude) : [];
  const title = ids?.length === 1 ? `Move ${forest.byId.get(ids[0])?.name ?? "element"} to a domain` : `Move ${ids?.length ?? 0} elements to a domain`;
  return (
    <Dialog open={!!ids} onOpenChange={(open) => !open && onClose()}>
      {ids ? (
        <DialogContent title={title}>
          <form
            className="flex flex-col gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              onMove(target || null);
            }}
          >
            <Field label="Domain" htmlFor="move-domain">
              <Select id="move-domain" value={target} onChange={(e) => setTarget(e.target.value)}>
                <option value="">{GROUP_LABELS.notInDomain}</option>
                {choices.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.path}
                  </option>
                ))}
              </Select>
            </Field>
            {leaving.length ? (
              <div role="alert" className="flex flex-col gap-1 rounded-control border border-default bg-app p-2 text-12" data-testid="move-scope-warning">
                <p className="font-medium">These marks are declared by a domain the new place is not in; they will be reported until you change them:</p>
                <ul className="list-disc pl-4">
                  {leaving.map((l) => (
                    <li key={l.name}>
                      {l.name}: {[...l.tags.map((t) => `tag ${t}`), ...(l.category ? [`category ${categoryName(l.category)}`] : [])].join(", ")}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit">{leaving.length ? "Move anyway" : "Move"}</Button>
            </div>
          </form>
        </DialogContent>
      ) : null}
    </Dialog>
  );
}

/**
 * Delete (1.8): the explorer opens it with the names of what to delete; it hands them straight to the explorer's
 * remove action (`onDelete`), which records the ids, and shows the delete plan dialog for them: what each way
 * through deletes, removes and clears, then one batch and one undo step.
 */
export function DeleteDialog({ names, onDelete }: { names: string[] | null; onClose: () => void; onDelete: () => void }) {
  const handOver = useRef(onDelete);
  handOver.current = onDelete;
  useEffect(() => {
    if (names) handOver.current();
  }, [names]);
  const request = useDeleteRequest();
  const { queryClient, drafts, store } = useServices();
  const first = request ? (request.names.get(request.ids[0]) ?? request.ids[0]) : "";
  const title = !request ? "" : request.ids.length === 1 ? `Delete ${first}?` : `Delete ${request.ids.length} elements?`;
  const run = async (resolution: DeleteResolution, plan: DeletePlan) => {
    const target = request;
    requestDelete(null);
    if (!target) return;
    const what = target.ids.length === 1 ? first : `${target.ids.length} elements`;
    const label =
      resolution === "delete-dependents"
        ? `Delete ${what} with dependents`
        : resolution === "remove-references"
          ? `Delete ${what} and clear references`
          : `Delete ${what}`;
    const reason = await runPlannedDelete({ queryClient, drafts, store }, { ids: target.ids, resolution, plan, label });
    if (reason) store.getState().notify(`Not deleted: ${reason}`, "error");
  };
  return <DeletePlanDialog ids={request?.ids ?? null} title={title} onClose={() => requestDelete(null)} onDelete={(r, p) => void run(r, p)} />;
}

/** Add with related… (1.8): the diagram and the depth of the relation walk. */
export function AddRelatedDialog({
  names,
  diagram,
  onClose,
  onAdd,
}: {
  names: string[] | null;
  /** The active diagram's name, or null when no diagram is open (a domain's view cannot take members). */
  diagram: string | null;
  onClose: () => void;
  onAdd: (depth: number) => void;
}) {
  const [depth, setDepth] = useState("1");
  const what = names?.length === 1 ? names[0] : `${names?.length ?? 0} entities`;
  return (
    <Dialog open={!!names} onOpenChange={(open) => !open && onClose()}>
      {names ? (
        <DialogContent
          title={`Add ${what} with related`}
          description={
            diagram
              ? `Adds the entities within the chosen number of relationship hops, and the relationships between them, to ${diagram}.`
              : "Open a diagram first: a domain's view already shows all of its entities."
          }
        >
          <form
            className="flex flex-col gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              onAdd(Number(depth));
            }}
          >
            <Field label="Depth" htmlFor="add-related-depth">
              <Select id="add-related-depth" value={depth} onChange={(e) => setDepth(e.target.value)}>
                {["1", "2", "3"].map((d) => (
                  <option key={d} value={d}>
                    {d === "1" ? "1 hop" : `${d} hops`}
                  </option>
                ))}
              </Select>
            </Field>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" disabled={!diagram} data-testid="confirm-add-related">
                Add
              </Button>
            </div>
          </form>
        </DialogContent>
      ) : null}
    </Dialog>
  );
}

/** Map to database… (D46): the database the selected domains or entities are mapped to. */
export function MapToDatabaseDialog({
  databases,
  count,
  onClose,
  onMap,
}: {
  databases: readonly { id: string; name: string }[] | null;
  count: number;
  onClose: () => void;
  onMap: (database: { id: string; name: string }) => void;
}) {
  const [target, setTarget] = useState("");
  if (!databases) return null;
  const chosen = databases.find((d) => d.id === target) ?? databases[0];
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={count === 1 ? "Map to database" : `Map ${count} elements to database`}>
        <form
          className="flex flex-col gap-2"
          data-testid="map-to-database-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            if (chosen) onMap(chosen);
          }}
        >
          {databases.length ? (
            <Field label="Database" htmlFor="map-to-database-target" hint="A domain joins the database's convention list; an entity gets a mapping element.">
              <Select id="map-to-database-target" value={chosen?.id ?? ""} onChange={(e) => setTarget(e.target.value)}>
                {databases.map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                  </option>
                ))}
              </Select>
            </Field>
          ) : (
            <p className="text-12 text-secondary">Create a database first.</p>
          )}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!chosen} data-testid="map-to-database-apply">
              Map
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
