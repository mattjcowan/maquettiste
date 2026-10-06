// The snapshot dialogs: take (packs left out unless ticked), rename or describe, delete, compare with another side, and
// restore with what it does said first and its undo ("restore the safety snapshot") as a button after.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import type { SnapshotInfo, SnapshotRestoreResult } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { ApiProblem } from "@/api/client";
import { keys } from "@/api/queries";
import { useServices } from "@/app/context";
import { useSnapshots } from "./queries";
import {
  applySnapshotScope,
  openCompare,
  openSnapshotDialog,
  snapshotTime,
  useAsOfNavigation,
  useSnapshotScope,
  useSnapshotUi,
  WORKING,
  type SnapshotDialog,
} from "./state";

export function SnapshotDialogs() {
  const dialog = useSnapshotUi((s) => s.dialog);
  if (!dialog) return null;
  const close = () => openSnapshotDialog(null);
  switch (dialog.kind) {
    case "take":
      return <TakeDialog close={close} />;
    case "edit":
      return <EditDialog snapshot={dialog.snapshot} close={close} />;
    case "delete":
      return <DeleteDialog snapshot={dialog.snapshot} close={close} />;
    case "compare-with":
      return <CompareWithDialog snapshot={dialog.snapshot} close={close} />;
    case "restore":
      return <RestoreDialog key={dialog.snapshot.id} snapshot={dialog.snapshot} close={close} />;
    case "restored":
      return <RestoredDialog result={dialog.result} close={close} />;
  }
}

const documents = (n: number) => `${n.toLocaleString()} ${n === 1 ? "document" : "documents"}`;
const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));

function TakeDialog({ close }: { close: () => void }) {
  const { store } = useServices();
  const qc = useQueryClient();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [packs, setPacks] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const take = async () => {
    setBusy(true);
    setError(null);
    try {
      const info = await endpoints.createSnapshot({ name: name.trim(), description: description.trim() || null, includePacks: packs });
      await qc.invalidateQueries({ queryKey: keys.snapshots });
      store.getState().notify(`Snapshot ${info.name} taken (${info.elements.toLocaleString()} elements).`);
      close();
    } catch (e) {
      setError(messageOf(e));
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent title="Take a snapshot" description="A read-only copy of the whole working model as it is now, kept under .maquettiste/model-snapshots/.">
        <form
          className="flex flex-col gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (name.trim()) void take();
          }}
          data-testid="snapshot-take-dialog"
        >
          <Field label="Name" htmlFor="snapshot-name">
            <Input id="snapshot-name" value={name} maxLength={200} onChange={(e) => setName(e.target.value)} autoFocus placeholder="Release 1 model" />
          </Field>
          <Field label="Description" htmlFor="snapshot-description">
            <Textarea id="snapshot-description" value={description} maxLength={4000} onChange={(e) => setDescription(e.target.value)} />
          </Field>
          <CheckboxField id="snapshot-packs" label="Include the template packs" checked={packs} onChange={setPacks} />
          <p className="text-11 text-secondary">
            Leave the packs out unless generating from this snapshot later must reproduce exactly: they are copied whole.
          </p>
          {error ? (
            <p role="alert" className="text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button onClick={close}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || busy} data-testid="snapshot-take-submit">
              {busy ? "Taking…" : "Take snapshot"}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function EditDialog({ snapshot, close }: { snapshot: SnapshotInfo; close: () => void }) {
  const qc = useQueryClient();
  const [name, setName] = useState(snapshot.name);
  const [description, setDescription] = useState(snapshot.description);
  const [error, setError] = useState<string | null>(null);
  const save = async () => {
    try {
      await endpoints.updateSnapshot(snapshot.id, { name: name.trim(), description });
      await qc.invalidateQueries({ queryKey: keys.snapshots });
      close();
    } catch (e) {
      setError(messageOf(e));
    }
  };
  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent title={`Rename ${snapshot.name}`} description={`The id (${snapshot.id}) and the documents stay as they are.`}>
        <form
          className="flex flex-col gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (name.trim()) void save();
          }}
        >
          <Field label="Name" htmlFor="snapshot-edit-name">
            <Input id="snapshot-edit-name" value={name} maxLength={200} onChange={(e) => setName(e.target.value)} autoFocus />
          </Field>
          <Field label="Description" htmlFor="snapshot-edit-description">
            <Textarea id="snapshot-edit-description" value={description} maxLength={4000} onChange={(e) => setDescription(e.target.value)} />
          </Field>
          {error ? (
            <p role="alert" className="text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button onClick={close}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!name.trim()} data-testid="snapshot-edit-save">
              Save
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function DeleteDialog({ snapshot, close }: { snapshot: SnapshotInfo; close: () => void }) {
  const qc = useQueryClient();
  const scope = useSnapshotScope();
  const { back } = useAsOfNavigation();
  const [error, setError] = useState<string | null>(null);
  const remove = async () => {
    try {
      await endpoints.deleteSnapshot(snapshot.id);
      if (scope === snapshot.id) back();
      await qc.invalidateQueries({ queryKey: keys.snapshots });
      close();
    } catch (e) {
      setError(messageOf(e));
    }
  };
  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent
        title={`Delete ${snapshot.name}?`}
        description={`Its archive (${snapshot.id}.zip, taken ${snapshotTime(snapshot.createdUtc)}) is removed. This cannot be undone.`}
      >
        {error ? (
          <p role="alert" className="text-danger">
            {error}
          </p>
        ) : null}
        <div className="flex justify-end gap-2">
          <Button onClick={close}>Cancel</Button>
          <Button variant="danger" onClick={() => void remove()} data-testid="snapshot-delete-confirm">
            Delete snapshot
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function CompareWithDialog({ snapshot, close }: { snapshot: SnapshotInfo; close: () => void }) {
  const list = useSnapshots();
  const others = (list.data ?? []).filter((s) => s.id !== snapshot.id);
  const [to, setTo] = useState(WORKING);
  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent title={`Compare ${snapshot.name} with…`} description="What changed from this snapshot to the other side.">
        <Field label="Compare with" htmlFor="snapshot-compare-to">
          <Select id="snapshot-compare-to" value={to} onChange={(e) => setTo(e.target.value)} data-testid="snapshot-compare-to">
            <option value={WORKING}>Working model</option>
            {others.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name} ({snapshotTime(s.createdUtc)})
              </option>
            ))}
          </Select>
        </Field>
        <div className="flex justify-end gap-2">
          <Button onClick={close}>Cancel</Button>
          <Button variant="primary" onClick={() => openCompare(snapshot.id, to)} data-testid="snapshot-compare-go">
            Compare
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

/** After a restore, the working model is what the snapshot held: every view reads it again. */
function useAfterRestore() {
  const { queryClient } = useServices();
  const scope = useSnapshotScope();
  const { back } = useAsOfNavigation();
  return () => {
    // Shown as of a snapshot: back to the working model at once (the URL follows), then everything is read again.
    applySnapshotScope(queryClient, null);
    if (scope) back();
    void queryClient.invalidateQueries();
  };
}

function RestoreDialog({ snapshot, close }: { snapshot: SnapshotInfo; close: () => void }) {
  const [packs, setPacks] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const after = useAfterRestore();
  const qc = useQueryClient();
  const restore = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await endpoints.restoreSnapshot(snapshot.id, packs && snapshot.includesPacks);
      if (result.outcome !== "restored") {
        setError(result.diagnostics[0]?.message ?? `The restore was refused (${result.outcome}); nothing changed.`);
        setBusy(false);
        return;
      }
      await qc.invalidateQueries({ queryKey: keys.snapshots });
      after();
      openSnapshotDialog({ kind: "restored", result } satisfies SnapshotDialog);
    } catch (e) {
      const problem = e instanceof ApiProblem ? e : null;
      setError(
        problem?.code === "run-locked" ? "A generation run is writing files right now. Nothing changed: restore the snapshot when the run ends." : messageOf(e),
      );
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent
        title={`Restore ${snapshot.name}?`}
        description={`Taken ${snapshotTime(snapshot.createdUtc)}${snapshot.author ? ` by ${snapshot.author}` : ""}.`}
      >
        <ul className="flex list-disc flex-col gap-1 pl-4" data-testid="snapshot-restore-steps">
          <li>
            A safety snapshot of the working model is taken first (<span className="font-mono">before-restore-…</span>); restoring it undoes this restore.
          </li>
          <li>The working model is then replaced by the snapshot: documents that differ are written, documents it does not have are deleted.</li>
          <li>
            {snapshot.includesPacks
              ? "The template packs are restored only if you tick the box below."
              : "This snapshot does not hold the template packs; the current packs stay as they are."}
          </li>
          <li>Other open windows see the change as soon as it is written. Nothing is validated: the model comes back as it was taken.</li>
        </ul>
        <CheckboxField
          id="snapshot-restore-packs"
          label="Also restore the template packs"
          checked={packs && snapshot.includesPacks}
          onChange={setPacks}
          disabled={!snapshot.includesPacks}
        />
        {error ? (
          <p role="alert" className="text-danger" data-testid="snapshot-restore-error">
            {error}
          </p>
        ) : null}
        <div className="flex justify-end gap-2">
          <Button onClick={close}>Cancel</Button>
          <Button variant="danger" disabled={busy} onClick={() => void restore()} data-testid="snapshot-restore-confirm">
            {busy ? "Restoring…" : "Restore snapshot"}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function RestoredDialog({ result, close }: { result: SnapshotRestoreResult; close: () => void }) {
  const qc = useQueryClient();
  const after = useAfterRestore();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const safety = result.safety;
  const undo = async () => {
    if (!safety) return;
    setBusy(true);
    setError(null);
    try {
      const undone = await endpoints.restoreSnapshot(safety.id, result.packsRestored);
      if (undone.outcome !== "restored") throw new Error(undone.diagnostics[0]?.message ?? `The undo was refused (${undone.outcome}).`);
      await qc.invalidateQueries({ queryKey: keys.snapshots });
      after();
      openSnapshotDialog({ kind: "restored", result: undone });
    } catch (e) {
      setError(e instanceof ApiProblem && e.code === "run-locked" ? "A generation run is writing files right now; undo when it ends." : messageOf(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : close())}>
      <DialogContent title={`Restored ${result.snapshot?.name ?? "the snapshot"}`} description="The working model is now what the snapshot held.">
        <p data-testid="snapshot-restored-summary">
          {documents(result.written)} written, {result.deleted.toLocaleString()} deleted
          {result.packsRestored ? ", template packs restored" : ""}.
        </p>
        {safety ? (
          <p className="text-secondary">
            The model as it was before is kept as <span className="font-mono">{safety.id}</span>.
          </p>
        ) : null}
        {error ? (
          <p role="alert" className="text-danger">
            {error}
          </p>
        ) : null}
        <div className="flex justify-end gap-2">
          {safety ? (
            <Button disabled={busy} onClick={() => void undo()} data-testid="snapshot-undo-restore">
              Undo: restore {safety.id}
            </Button>
          ) : null}
          <Button variant="primary" onClick={close}>
            Done
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
