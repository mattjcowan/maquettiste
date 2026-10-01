// Remove pack (the pack editor's header): a confirmation that names what goes (the pack's folder and its packs.<pack>
// settings entry) and what stays (the files it generated, counted from its manifest, under their output roots), then
// DELETE /api/packs/{pack} with the pack.json hash seen when the dialog opened. The pack's tab closes as the removal
// starts (the Generate screen shows the next tab, or the Plan screen with the pack list beside it) and opens again when
// the removal is refused. There is no undo: the folder is gone from disk.
import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, usePackOutputs } from "@/api/queries";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { closePackTab, openPackTab } from "./packTabs";
import { discardDrafts, hasUnsaved } from "./drafts";

/** The sentence about the generated files a removal leaves on disk. */
export function untrackedSentence(count: number | null, roots: string[]): string {
  if (count === null) return "Counting the files it generated…";
  if (count === 0) return "It has generated no files.";
  const where = roots.length ? ` under ${roots.join(", ")}` : "";
  return `The ${count} ${count === 1 ? "file" : "files"} it generated${where} stay on disk and are no longer tracked; delete them yourself if you do not want them.`;
}

export function RemovePackButton({ pack, hash, fileCount, outputBase }: { pack: string; hash: string; fileCount: number; outputBase: string }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button
        size="sm"
        variant="ghost"
        className="ml-auto text-danger"
        title={`Delete .maquettiste/templates/${pack}/ and its settings; generated files stay on disk`}
        onClick={() => setOpen(true)}
        data-testid="pack-remove"
      >
        <Trash2 className="size-3.5" aria-hidden /> Remove pack…
      </Button>
      {open ? <RemovePackDialog pack={pack} hash={hash} fileCount={fileCount} outputBase={outputBase} onClose={() => setOpen(false)} /> : null}
    </>
  );
}

function RemovePackDialog({
  pack,
  hash,
  fileCount,
  outputBase,
  onClose,
}: {
  pack: string;
  hash: string;
  fileCount: number;
  outputBase: string;
  onClose(): void;
}) {
  const { store } = useServices();
  const qc = useQueryClient();
  const outputs = usePackOutputs(pack);
  // The pack.json hash the user saw when the dialog opened: a change since is a conflict, not a silent removal.
  const [seen] = useState(hash);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  // The manifest as it is now: a cached answer from before the last apply would undercount.
  const { refetch } = outputs;
  useEffect(() => void refetch(), [refetch]);
  const written = outputs.isFetchedAfterMount ? (outputs.data?.outputs ?? null) : null;
  const roots = written ? [...new Set(written.map((o) => o.root ?? o.path.split("/")[0]))].sort() : [];
  const unsaved = hasUnsaved(pack);
  const conflict = "pack.json changed since this editor read it; nothing was removed. Close this dialog to reload the pack, then try again.";
  const remove = async () => {
    setBusy(true);
    setFailure(null);
    let closed = false;
    try {
      // A change since the dialog opened is answered here, while the dialog can still say so.
      if ((await endpoints.getPack(pack)).hash !== seen) {
        setFailure(conflict);
        await qc.invalidateQueries({ queryKey: keys.pack(pack) });
        return;
      }
      // The editor closes and the pack leaves the cached list first: once it is gone, nothing open may read it again
      // (the change events of the removal refresh every pack query still observed).
      store.getState().setGeneration(closePackTab(store.getState().generation, pack));
      qc.setQueryData<endpoints.PackSummary[]>(keys.packs, (list) => list?.filter((p) => p.name !== pack));
      qc.removeQueries({ queryKey: keys.pack(pack) });
      closed = true;
      const result = await endpoints.deletePack(pack, seen);
      if (result.outcome !== "saved") {
        store.getState().setGeneration(openPackTab(store.getState().generation, pack));
        await qc.invalidateQueries({ queryKey: keys.packs });
        store
          .getState()
          .notify(
            result.outcome === "conflict"
              ? conflict
              : `The pack was not removed: ${result.diagnostics.map((d) => `${d.rule} ${d.message}`).join("; ") || result.outcome}.`,
            "error",
          );
        return;
      }
      discardDrafts(pack);
      await Promise.all([
        qc.invalidateQueries({ queryKey: keys.packs, exact: true }),
        qc.invalidateQueries({ queryKey: keys.settings }),
        qc.invalidateQueries({ queryKey: keys.project }),
      ]);
      const left = result.untracked.length;
      store
        .getState()
        .notify(
          `Removed .maquettiste/templates/${pack}/.${left ? ` ${left} generated ${left === 1 ? "file stays" : "files stay"} on disk, untracked.` : ""}`,
          "info",
        );
    } catch (e) {
      if (closed) {
        store.getState().setGeneration(openPackTab(store.getState().generation, pack));
        void qc.invalidateQueries({ queryKey: keys.packs });
        store.getState().notify(`The pack was not removed: ${(e as Error).message}`, "error");
      } else setFailure((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(o) => (o ? undefined : onClose())}>
      <DialogContent title={`Remove pack ${pack}`} description="Deletes the pack's templates and settings; generated files are not deleted.">
        <div className="flex flex-col gap-2" data-testid="remove-pack-dialog">
          <p>
            Deletes <span className="font-mono">.maquettiste/templates/{pack}/</span> ({fileCount} {fileCount === 1 ? "file" : "files"}) and{" "}
            <span className="font-mono">packs.{pack}</span> in maquettiste.json
            {outputBase ? (
              <>
                {" "}
                (output base <span className="font-mono">{outputBase}</span>)
              </>
            ) : null}
            .
          </p>
          <p data-testid="remove-pack-untracked">
            {outputs.isError ? "The files it generated could not be counted; they stay on disk." : untrackedSentence(written ? written.length : null, roots)}
          </p>
          {unsaved ? <p className="text-warning">The unsaved edits in this pack are discarded.</p> : null}
          {failure ? (
            <p role="alert" className="whitespace-pre-wrap text-danger">
              {failure}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="button" variant="danger" disabled={busy} onClick={() => void remove()} data-testid="remove-pack-confirm">
              Remove pack
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
