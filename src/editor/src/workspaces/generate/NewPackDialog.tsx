// New pack (generation-ui.md 2.1): a name (`^[a-z][a-z0-9-]*$`, unique) and Start from: Empty pack (pack.json with one
// `each entity` unit and its template, as `pack new --from empty` writes) or Copy of a pack of this project, under the
// new name; a line under the choice says what it gives. It calls POST /api/packs, then opens the new pack's editor on
// Units. The dialog says what gets written.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys, usePacks } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { useEditor } from "@/state/store";
import { openPackTab } from "./packTabs";

export const PACK_NAME = /^[a-z][a-z0-9-]*$/;
/** The files `pack new --from empty` writes (PackAuthoring.NewPack): pack.json and the unit's template. */
export const EMPTY_PACK_FILES = 2;

/** The line under Start from: what the choice gives. */
export function startFromText(from: string, count: number, name: string): string {
  if (from === "empty") return "One each-entity unit and its template, ready to edit.";
  return `All ${count} ${count === 1 ? "file" : "files"} of ${from}, renamed to ${name || "<name>"}.`;
}

export function newPackError(name: string, taken: string[]): string | null {
  if (!name) return "Enter a name.";
  if (!PACK_NAME.test(name)) return "Lowercase letters, digits and hyphens, starting with a letter.";
  if (taken.includes(name)) return `A pack named ${name} exists.`;
  return null;
}

export function NewPackDialog() {
  const { store } = useServices();
  const qc = useQueryClient();
  const { openWorkspace } = useEditorNavigation();
  const open = useEditor(store, (s) => s.generation.newPack);
  const packs = usePacks();
  const [name, setName] = useState("");
  const [from, setFrom] = useState("empty");
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const list = packs.data ?? [];
  const error = newPackError(
    name,
    list.map((p) => p.name),
  );
  const count = from === "empty" ? EMPTY_PACK_FILES : (list.find((p) => p.name === from)?.fileCount ?? 0);
  const close = () => {
    store.getState().setGeneration({ newPack: false });
    setName("");
    setFrom("empty");
    setFailure(null);
  };
  const create = async () => {
    if (error) return;
    setBusy(true);
    setFailure(null);
    try {
      const result = await endpoints.createPack(name, from);
      if (result.outcome !== "saved") {
        setFailure(result.diagnostics.map((d) => `${d.rule} ${d.message}`).join("\n") || `The pack could not be created (${result.outcome}).`);
        return;
      }
      await qc.invalidateQueries({ queryKey: keys.packs });
      store.getState().setGeneration({ ...openPackTab(store.getState().generation, name, "units"), newPack: false });
      if (store.getState().workspace !== "generate") openWorkspace("generate");
      store.getState().notify(`Created .maquettiste/templates/${name}/`, "info");
      setName("");
      setFrom("empty");
    } catch (e) {
      setFailure((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open={open} onOpenChange={(o) => (o ? undefined : close())}>
      <DialogContent title="New pack" description="A folder of templates and one pack.json under .maquettiste/templates/.">
        <form
          className="flex flex-col gap-2"
          data-testid="new-pack-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void create();
          }}
        >
          <Field label="Name" htmlFor="new-pack-name" hint={name && error ? error : undefined}>
            <Input id="new-pack-name" autoFocus value={name} onChange={(e) => setName(e.target.value.trim())} aria-invalid={!!name && !!error} />
          </Field>
          <Field label="Start from" htmlFor="new-pack-from">
            <Select id="new-pack-from" value={from} onChange={(e) => setFrom(e.target.value)}>
              <option value="empty">Empty pack</option>
              {list.map((p) => (
                <option key={p.name} value={p.name}>
                  Copy of {p.name}
                </option>
              ))}
            </Select>
          </Field>
          <p className="-mt-1 text-12" data-testid="new-pack-from-text">
            {startFromText(from, count, name)}
          </p>
          <p className="text-12 text-secondary" data-testid="new-pack-writes">
            Creates .maquettiste/templates/{name || "<name>"}/ with {count} {count === 1 ? "file" : "files"}.
          </p>
          {failure ? (
            <p role="alert" className="whitespace-pre-wrap text-12 text-danger">
              {failure}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" disabled={!!error || busy} data-testid="new-pack-create">
              Create
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
