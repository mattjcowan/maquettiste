// Rename pack (the pack editor's header, beside Remove pack…): a name under the pack-name rule, the folder it moves to,
// then POST /api/packs/{pack}/rename with the pack.json hash seen when the dialog opened. The folder, the packs.<pack>
// settings entry, the manifests and the unit states move together, so the files the pack generated stay tracked. The
// dialog lives beside the Generate explorer, not in the pack editor: the pack's tab closes while the request runs (nothing
// may read the old name meanwhile) and comes back under the new name in the same place, or under the old one when the
// rename is refused, with the dialog still open to say why. It is not an undo step: rename the pack back the same way.
// Generation hints keyed by the old name (`generation.<pack>`) are counted by a dry run of the rename once the name is
// valid; "Also update the generation hints…" (ticked by default) moves them to the new name after the rename, as one
// model batch through the normal element save path, so that part is one undo step.
import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { PencilLine } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, usePackOutputs, usePacks } from "@/api/queries";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { useBatchEdit } from "@/explorer/marks";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input } from "@/components/ui/input";
import { useEditor } from "@/state/store";
import { hasUnsaved } from "./drafts";
import { renamePackHints } from "./packHints";
import { closePackTab, renamePackTab } from "./packTabs";

/** The engine's pack-name rule (PackAuthoring.NamePattern). */
export const PACK_NAME_RULE = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;
export const PACK_NAME_HINT = "Lowercase letters and digits separated by single hyphens, starting with a letter.";

/** Why `name` cannot be the pack's new name, or null when it can. */
export function renamePackError(name: string, current: string, taken: string[]): string | null {
  if (!name) return "Enter a name.";
  if (name === current) return "Enter a different name.";
  if (!PACK_NAME_RULE.test(name)) return PACK_NAME_HINT;
  if (taken.includes(name)) return `A pack named ${name} exists.`;
  return null;
}

/** The label of the hint update choice. */
export function hintsLabel(count: number): string {
  return `Also update the generation hints that name this pack (${count} ${count === 1 ? "element" : "elements"})`;
}

/** The sentence about the generated files a rename keeps tracked. */
export function trackedSentence(count: number | null): string {
  if (count === null) return "Counting the files it generated…";
  if (count === 0) return "It has generated no files.";
  return `The ${count} ${count === 1 ? "file" : "files"} it generated stay where they are and stay tracked under the new name.`;
}

export function RenamePackButton({ pack, hash }: { pack: string; hash: string }) {
  const { store } = useServices();
  return (
    <Button
      size="sm"
      variant="ghost"
      title={`Rename the pack: moves .maquettiste/templates/${pack}/ and its settings; generated files stay tracked`}
      onClick={() => store.getState().setGeneration({ renamePack: { pack, hash } })}
      data-testid="pack-rename"
    >
      <PencilLine className="size-3.5" aria-hidden /> Rename pack…
    </Button>
  );
}

/** Mounted once beside the Generate explorer; open while `generation.renamePack` is set. */
export function RenamePackDialog({ onRenamed }: { onRenamed?(from: string, to: string): void }) {
  const { store } = useServices();
  const target = useEditor(store, (s) => s.generation.renamePack);
  if (!target) return null;
  return <RenameForm key={target.pack} pack={target.pack} seen={target.hash} onRenamed={onRenamed} />;
}

function RenameForm({ pack, seen, onRenamed }: { pack: string; seen: string; onRenamed?(from: string, to: string): void }) {
  const { store } = useServices();
  const qc = useQueryClient();
  const packs = usePacks();
  const [name, setName] = useState(pack);
  const [busy, setBusy] = useState(false);
  // While the folder moves, nothing reads the old name (it answers 404 once the move is done).
  const [moving, setMoving] = useState(false);
  const outputs = usePackOutputs(pack, !moving);
  const [failure, setFailure] = useState<string | null>(null);
  // The manifest as it is now: a cached answer from before the last apply would undercount.
  const { refetch } = outputs;
  useEffect(() => void refetch(), [refetch]);
  const written = outputs.isFetchedAfterMount ? (outputs.data?.outputs.length ?? null) : null;
  const error = renamePackError(
    name,
    pack,
    (packs.data ?? []).map((p) => p.name),
  );
  const unsaved = hasUnsaved(pack);
  const [updateHints, setUpdateHints] = useState(true);
  const batchEdit = useBatchEdit();
  // A dry run of the rename once the name is valid: it counts the hints to update and runs the server's own checks.
  const preview = useQuery({
    queryKey: ["pack-rename-preview", pack, name, seen],
    queryFn: () => endpoints.renamePack(pack, name, seen, true),
    enabled: !error && !moving && !unsaved,
    retry: false,
  });
  const refused = preview.data && preview.data.outcome !== "saved" ? preview.data : null;
  const hints = preview.data?.outcome === "saved" ? preview.data.hints : [];
  const conflict = "pack.json changed since this editor read it; nothing was renamed. Close this dialog to reload the pack, then try again.";
  const close = () => store.getState().setGeneration({ renamePack: null });
  const rename = async () => {
    if (error || unsaved || refused) return;
    setBusy(true);
    setFailure(null);
    const before = store.getState().generation;
    let closed = false;
    try {
      // A change since the dialog opened is answered here, while nothing has moved.
      if ((await endpoints.getPack(pack)).hash !== seen) {
        setFailure(conflict);
        await qc.invalidateQueries({ queryKey: keys.pack(pack) });
        return;
      }
      // The editor closes, the pack leaves the cached list and its queries go first: once the folder moves, nothing open
      // may read the old name again (the change events of the rename bring the list back with the new name).
      setMoving(true);
      store.getState().setGeneration(closePackTab(store.getState().generation, pack));
      qc.setQueryData<endpoints.PackSummary[]>(keys.packs, (list) => list?.filter((p) => p.name !== pack));
      qc.removeQueries({ queryKey: keys.pack(pack) });
      closed = true;
      const result = await endpoints.renamePack(pack, name, seen);
      if (result.outcome !== "saved") {
        store.getState().setGeneration(pick(before));
        setMoving(false);
        closed = false;
        await qc.invalidateQueries({ queryKey: keys.packs });
        setFailure(
          result.outcome === "conflict" ? conflict : result.diagnostics.map((d) => d.message).join("\n") || `The pack was not renamed (${result.outcome}).`,
        );
        return;
      }
      // The hints move as one model batch (one undo step), each element through the normal save path.
      const owners = result.hints;
      const moved =
        updateHints && owners.length > 0
          ? await batchEdit(`Move generation hints ${pack} to ${name}`, owners, (json) => void renamePackHints(json, pack, name))
          : false;
      store.getState().setGeneration({ ...pick(renamePackTab(before, pack, name)), renamePack: null });
      onRenamed?.(pack, name);
      await Promise.all([
        qc.invalidateQueries({ queryKey: keys.packs, exact: true }),
        qc.invalidateQueries({ queryKey: keys.settings }),
        qc.invalidateQueries({ queryKey: keys.project }),
      ]);
      const count = owners.length;
      const elements = `${count} ${count === 1 ? "element" : "elements"}`;
      if (moved) store.getState().notify(`Renamed pack ${pack} to ${name} and moved the generation hints of ${elements}.`, "info");
      else if (count && updateHints)
        store
          .getState()
          .notify(`Renamed pack ${pack} to ${name}, but the generation hints of ${elements} still name ${pack}: the hint update was refused.`, "error");
      else store.getState().notify(`Renamed pack ${pack} to ${name}.${count ? ` The generation hints of ${elements} still name ${pack}.` : ""}`, "info");
    } catch (e) {
      if (closed) {
        store.getState().setGeneration(pick(before));
        setMoving(false);
        void qc.invalidateQueries({ queryKey: keys.packs });
      }
      setFailure((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(o) => (o ? undefined : close())}>
      <DialogContent title={`Rename pack ${pack}`} description="Moves the pack's folder and its settings to a new name.">
        <form
          className="flex flex-col gap-2"
          data-testid="rename-pack-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void rename();
          }}
        >
          <Field label="Name" htmlFor="rename-pack-name" hint={name !== pack && error ? error : PACK_NAME_HINT}>
            <Input
              id="rename-pack-name"
              autoFocus
              onFocus={(e) => e.target.select()}
              value={name}
              onChange={(e) => setName(e.target.value.trim())}
              aria-invalid={name !== pack && !!error}
            />
          </Field>
          <p className="text-12" data-testid="rename-pack-moves">
            Moves <span className="font-mono">.maquettiste/templates/{pack}/</span> to{" "}
            <span className="font-mono">.maquettiste/templates/{name || "<name>"}/</span> and <span className="font-mono">packs.{pack}</span> to{" "}
            <span className="font-mono">packs.{name || "<name>"}</span> in maquettiste.json.
          </p>
          <p className="text-12" data-testid="rename-pack-tracked">
            {outputs.isError ? "The files it generated could not be counted; they stay tracked." : trackedSentence(written)}
          </p>
          {hints.length ? (
            <div data-testid="rename-pack-hints">
              <CheckboxField id="rename-pack-update-hints" label={hintsLabel(hints.length)} checked={updateHints} onChange={setUpdateHints} />
            </div>
          ) : null}
          {refused ? (
            <p role="alert" className="whitespace-pre-wrap text-12 text-danger" data-testid="rename-pack-refused">
              {refused.outcome === "conflict"
                ? conflict
                : refused.diagnostics.map((d) => d.message).join("\n") || `The pack cannot be renamed (${refused.outcome}).`}
            </p>
          ) : null}
          <p className="text-12 text-secondary">This cannot be undone from the editor; rename the pack back the same way.</p>
          {unsaved ? <p className="text-12 text-warning">Save or discard the unsaved edits in this pack first.</p> : null}
          {failure ? (
            <p role="alert" className="whitespace-pre-wrap text-12 text-danger">
              {failure}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" disabled={!!error || unsaved || busy || !!refused} data-testid="rename-pack-confirm">
              Rename pack
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** The tab fields of the Generate state (what a rename changes). */
function pick<T extends { packTabs: string[]; packTab: string | null; packPane: object; packFocus: unknown; chosenPacks: string[] | null }>(state: T) {
  const { packTabs, packTab, packPane, packFocus, chosenPacks } = state;
  return { packTabs, packTab, packPane, packFocus, chosenPacks } as Pick<T, "packTabs" | "packTab" | "packPane" | "packFocus" | "chosenPacks">;
}
