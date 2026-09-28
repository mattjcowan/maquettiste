// The conflict dialog (phase2-design.md 4.6): a save answered 409. A Monaco diff of the disk
// version (left) against the draft (right, editable), with Keep mine (retry with the disk hash),
// Take theirs, and Save merge (the right side as edited).
import { useRef } from "react";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { CodeDiff } from "@/code";
import type { ModelJson } from "@/api/types";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";

export function ConflictDialog() {
  const { store, drafts } = useServices();
  const conflict = useEditor(store, (s) => Object.values(s.drafts).find((d) => d.status === "conflict") ?? null);
  const getModified = useRef<(() => string) | null>(null);
  if (!conflict) return null;
  const name = String((conflict.json as { name?: string }).name ?? conflict.id);
  const theirs = conflict.conflict ? JSON.stringify(conflict.conflict.json, null, 2) : "";
  const mine = JSON.stringify(conflict.json, null, 2);
  return (
    <Dialog open onOpenChange={() => undefined}>
      <DialogContent
        wide
        hideClose
        title={`${name} changed on disk`}
        description="Someone else saved this element (another window, a disk edit or the CLI) after you loaded it. Left: the disk version. Right: your changes, which you can edit to merge."
      >
        <div className="h-[55vh] min-h-0" data-testid="conflict-diff">
          <CodeDiff
            label={`Conflict for ${name}`}
            original={theirs}
            modified={mine}
            onMount={(get) => {
              getModified.current = get;
            }}
          />
        </div>
        <div className="flex flex-wrap justify-end gap-2">
          <Button onClick={() => drafts.takeTheirs(conflict.id)}>Take theirs</Button>
          <Button
            onClick={() => {
              const text = getModified.current?.() ?? mine;
              try {
                void drafts.merge(conflict.id, JSON.parse(text) as ModelJson);
              } catch (e) {
                store.getState().notify(`The merged document is not valid JSON: ${(e as Error).message}`, "error");
              }
            }}
          >
            Save merge
          </Button>
          <Button variant="primary" onClick={() => void drafts.keepMine(conflict.id)}>
            Keep mine
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
