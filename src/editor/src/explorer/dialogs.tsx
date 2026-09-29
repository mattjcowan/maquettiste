// The explorer's dialogs (explorer-redesign.md 1.8): Move to domain… (a domain's Open is the domain editor,
// editors/DomainEditor), the delete confirmation and Add with related… (canvas in step, step 10).
import { useState } from "react";
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
  const choices = ids ? domainChoices(forest, exclude) : [];
  const title = ids?.length === 1 ? `Move ${forest.byId.get(ids[0])?.name ?? "element"} to a domain` : `Move ${ids?.length ?? 0} elements to a domain`;
  return (
    <Dialog open={!!ids} onOpenChange={(open) => !open && onClose()}>
      {ids ? (
        <DialogContent title={title}>
          <form
            className="flex flex-col gap-3"
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
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit">Move</Button>
            </div>
          </form>
        </DialogContent>
      ) : null}
    </Dialog>
  );
}

export function DeleteDialog({ names, onClose, onDelete }: { names: string[] | null; onClose: () => void; onDelete: () => void }) {
  const title = names?.length === 1 ? `Delete ${names[0]}?` : `Delete ${names?.length ?? 0} elements?`;
  return (
    <Dialog open={!!names} onOpenChange={(open) => !open && onClose()}>
      {names ? (
        <DialogContent title={title} description="An element that is still referenced is not deleted; the editor says which.">
          {names.length > 1 ? <p className="max-h-40 overflow-auto text-13 text-secondary">{names.join(", ")}</p> : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="button" variant="danger" onClick={onDelete} data-testid="confirm-delete">
              Delete
            </Button>
          </div>
        </DialogContent>
      ) : null}
    </Dialog>
  );
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
            className="flex flex-col gap-3"
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
