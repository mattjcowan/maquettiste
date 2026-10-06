// The snapshot picker on the project name (docs/engineering/snapshots.md section 10): the working model and its snapshots,
// newest first, with a search, and per snapshot Open (as of), Compare, Restore, Export, Rename, Publish and Delete; Take
// snapshot and Import at the top. Each action names the role it needs and is disabled with that reason for a lesser role.
import { useMemo, useRef, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Camera, ChevronDown, MoreHorizontal, Upload } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge, Spinner } from "@/components/ui/misc";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/menu";
import type { SnapshotInfo } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { ApiProblem } from "@/api/client";
import { keys } from "@/api/queries";
import { useServices } from "@/app/context";
import { cn } from "@/lib/cn";
import { useSnapshots } from "./queries";
import {
  deniedReason,
  openCompare,
  openSnapshotDialog,
  snapshotTime,
  useAsOfNavigation,
  useRole,
  useSnapshotScope,
  WORKING,
  type SnapshotAction,
} from "./state";

/** The project name as the picker's trigger: the same text and size as before, with a small chevron. */
export function SnapshotPicker({ children, tooltip }: { children: ReactNode; tooltip: string }) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="flex min-w-0 items-center gap-0.5 rounded-control text-left hover:bg-accent-subtle"
          title={`${tooltip}. Click for the working model and its snapshots.`}
          aria-label="Model snapshots: the working model and its snapshots"
          data-testid="snapshot-picker"
        >
          {children}
          <ChevronDown className="size-3.5 shrink-0 text-secondary" aria-hidden />
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="flex w-[440px] flex-col gap-2 p-2" data-testid="snapshot-picker-panel" aria-label="Model snapshots">
        <PickerBody close={() => setOpen(false)} />
      </PopoverContent>
    </Popover>
  );
}

function PickerBody({ close }: { close: () => void }) {
  const list = useSnapshots();
  const scope = useSnapshotScope();
  const role = useRole();
  const { open, back } = useAsOfNavigation();
  const [text, setText] = useState("");
  const rows = useMemo(() => {
    const needle = text.trim().toLowerCase();
    const all = list.data ?? [];
    if (!needle) return all;
    return all.filter((s) => `${s.name} ${s.description} ${s.author} ${s.id}`.toLowerCase().includes(needle));
  }, [list.data, text]);

  return (
    <>
      <div className="flex items-center gap-1">
        <Input
          type="search"
          aria-label="Find a snapshot"
          placeholder="Find a snapshot"
          className="h-6 flex-1 text-12"
          value={text}
          onChange={(e) => setText(e.target.value)}
          data-testid="snapshot-search"
        />
        <Guarded reason={deniedReason(role, "take")}>
          <Button
            size="sm"
            variant="primary"
            disabled={!!deniedReason(role, "take")}
            onClick={() => {
              close();
              openSnapshotDialog({ kind: "take" });
            }}
            data-testid="snapshot-take"
          >
            <Camera />
            Take snapshot…
          </Button>
        </Guarded>
        <ImportButton disabledReason={deniedReason(role, "import")} close={close} />
      </div>
      <ul className="mq-scroll flex max-h-[60vh] flex-col overflow-y-auto" aria-label="Snapshots" data-testid="snapshot-list">
        <li>
          <button
            type="button"
            className={cn("flex w-full items-center gap-2 rounded-control px-1.5 py-1 text-left hover:bg-accent-subtle", !scope && "bg-accent-subtle")}
            onClick={() => {
              close();
              if (scope) back();
            }}
            data-testid="snapshot-row-working"
          >
            <span className="flex-1 font-medium">Working model</span>
            <Badge tone={scope ? "neutral" : "accent"}>{scope ? "current" : "shown"}</Badge>
          </button>
        </li>
        {list.isPending ? (
          <li className="p-2">
            <Spinner label="Reading snapshots" />
          </li>
        ) : list.isError ? (
          <li className="p-2 text-danger">{(list.error as Error).message}</li>
        ) : rows.length === 0 ? (
          <li className="p-2 text-secondary">{text ? "No snapshot matches." : "No snapshots yet. Take one to keep this model as it is now."}</li>
        ) : (
          rows.map((s) => <SnapshotRow key={s.id} snapshot={s} shown={scope === s.id} close={close} onOpen={() => void open(s.id)} />)
        )}
      </ul>
    </>
  );
}

/** A disabled control cannot show a tooltip (it takes no pointer events): the reason sits on a wrapper. */
function Guarded({ reason, children }: { reason: string | null; children: ReactNode }) {
  if (!reason) return children;
  return (
    <span title={reason} className="inline-flex" data-denied={reason}>
      {children}
    </span>
  );
}

function SnapshotRow({ snapshot: s, shown, close, onOpen }: { snapshot: SnapshotInfo; shown: boolean; close: () => void; onOpen: () => void }) {
  const role = useRole();
  const qc = useQueryClient();
  const { store, mock } = useServices();
  const when = snapshotTime(s.createdUtc);
  const run = (action: () => void) => () => {
    close();
    action();
  };
  const item = (action: SnapshotAction, label: string, onSelect: () => void, testid: string) => {
    const reason = deniedReason(role, action);
    return (
      <DropdownMenuItem disabled={!!reason} title={reason ?? undefined} onSelect={onSelect} data-testid={testid}>
        {label}
      </DropdownMenuItem>
    );
  };
  const togglePublished = async () => {
    try {
      await endpoints.updateSnapshot(s.id, { published: !s.published });
      await qc.invalidateQueries({ queryKey: keys.snapshots });
      store.getState().notify(s.published ? `${s.name} is no longer published.` : `${s.name} is published.`);
    } catch (e) {
      store.getState().notify((e as Error).message, "error");
    }
  };
  const download = async () => {
    try {
      if (!mock) {
        const a = document.createElement("a");
        a.href = endpoints.snapshotExportUrl(s.id);
        a.download = `${s.id}.zip`;
        a.click();
        return;
      }
      // The mock answers fetches only (its worker leaves downloads to the network): the bytes become a local link.
      const blob = await endpoints.exportSnapshot(s.id);
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `${s.id}.zip`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch (e) {
      store.getState().notify((e as ApiProblem).message, "error");
    }
  };

  return (
    <li
      className={cn("group flex items-start gap-1 rounded-control px-1.5 py-1 hover:bg-accent-subtle", shown && "bg-accent-subtle")}
      data-testid={`snapshot-row-${s.id}`}
    >
      <button
        type="button"
        className="flex min-w-0 flex-1 flex-col text-left"
        onClick={run(onOpen)}
        title={`Open ${s.name} read-only, as of ${when}`}
        data-testid="snapshot-open-row"
      >
        <span className="flex min-w-0 items-center gap-1">
          <span className="truncate font-medium" data-testid="snapshot-name">
            {s.name}
          </span>
          {s.published ? <Badge tone="success">Published</Badge> : null}
          {s.origin === "before-restore" ? <Badge tone="warning">Safety</Badge> : null}
          {s.includesPacks ? <Badge>Packs</Badge> : null}
          {shown ? <Badge tone="accent">shown</Badge> : null}
        </span>
        <span className="truncate text-11 text-secondary">
          {when}
          {s.author ? ` · ${s.author}` : ""} · {s.elements.toLocaleString()} elements
        </span>
        {s.description ? <span className="truncate text-11 text-secondary">{s.description}</span> : null}
      </button>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button size="icon-row" variant="ghost" label={`Actions for ${s.name}`} data-testid="snapshot-actions">
            <MoreHorizontal />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          {item("open", "Open (read-only, as of)", run(onOpen), "snapshot-open")}
          {item(
            "compare",
            "Compare with working",
            run(() => openCompare(s.id, WORKING)),
            "snapshot-compare-working",
          )}
          {item(
            "compare",
            "Compare with…",
            run(() => openSnapshotDialog({ kind: "compare-with", snapshot: s })),
            "snapshot-compare-with",
          )}
          <DropdownMenuSeparator />
          {item(
            "restore",
            "Restore…",
            run(() => openSnapshotDialog({ kind: "restore", snapshot: s })),
            "snapshot-restore",
          )}
          {item(
            "export",
            "Export (.zip)",
            run(() => void download()),
            "snapshot-export",
          )}
          <DropdownMenuSeparator />
          {item(
            "edit",
            "Rename or describe…",
            run(() => openSnapshotDialog({ kind: "edit", snapshot: s })),
            "snapshot-edit",
          )}
          {item("publish", s.published ? "Unpublish" : "Publish", () => void togglePublished(), "snapshot-publish")}
          <DropdownMenuSeparator />
          {item(
            "delete",
            "Delete…",
            run(() => openSnapshotDialog({ kind: "delete", snapshot: s })),
            "snapshot-delete",
          )}
        </DropdownMenuContent>
      </DropdownMenu>
    </li>
  );
}

function ImportButton({ disabledReason, close }: { disabledReason: string | null; close: () => void }) {
  const input = useRef<HTMLInputElement>(null);
  const { store } = useServices();
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const upload = async (file: File) => {
    setBusy(true);
    try {
      const result = await endpoints.importSnapshot(file);
      if (result.snapshot) {
        await qc.invalidateQueries({ queryKey: keys.snapshots });
        store.getState().notify(`Imported ${result.snapshot.name} as ${result.snapshot.id}.`);
      } else {
        const first = result.diagnostics[0];
        const where = first?.filePath ? ` (${first.filePath})` : "";
        store.getState().notify(`The archive was not imported: ${first?.message ?? "refused"}${where}`, "error");
      }
    } catch (e) {
      const problem = e as ApiProblem;
      store
        .getState()
        .notify(
          problem.status === 413
            ? `The archive was not imported: it is larger than the server accepts (${problem.message}).`
            : `The archive was not imported: ${problem.message}`,
          "error",
        );
    } finally {
      setBusy(false);
      close();
    }
  };
  return (
    <Guarded reason={disabledReason}>
      <Button
        size="icon-sm"
        variant="ghost"
        label="Import snapshot… (an exported .zip)"
        disabled={!!disabledReason || busy}
        onClick={() => input.current?.click()}
        data-testid="snapshot-import"
      >
        <Upload />
      </Button>
      <input
        ref={input}
        type="file"
        accept=".zip,application/zip"
        className="hidden"
        aria-label="Snapshot archive to import"
        data-testid="snapshot-import-file"
        onChange={(e) => {
          const file = e.target.files?.[0];
          e.target.value = "";
          if (file) void upload(file);
        }}
      />
    </Guarded>
  );
}
