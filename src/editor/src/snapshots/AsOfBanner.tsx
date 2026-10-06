// The as-of line in the top bar (no extra header row): which snapshot is shown, that it is read-only, and the way out.
import { History } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useSnapshots } from "./queries";
import { deniedReason, openCompare, openSnapshotDialog, snapshotTime, useAsOfNavigation, useRole, useSnapshotScope, WORKING } from "./state";

export function AsOfBanner() {
  const scope = useSnapshotScope();
  const list = useSnapshots(!!scope);
  const role = useRole();
  const { back } = useAsOfNavigation();
  if (!scope) return null;
  const info = list.data?.find((s) => s.id === scope);
  const missing = list.isSuccess && !info;
  const restoreDenied = deniedReason(role, "restore");
  return (
    <div
      role="status"
      className="flex h-6 min-w-0 max-w-[50%] shrink-0 items-center gap-1 rounded-control border border-warning bg-surface pl-1.5 text-12"
      data-testid="as-of-banner"
    >
      <History className="size-3.5 shrink-0 text-warning" aria-hidden />
      {missing ? (
        <span className="min-w-0 truncate">No snapshot has the id {scope}.</span>
      ) : (
        <>
          {/* The name and time give way first; "read-only" always shows. */}
          <span className="min-w-0 truncate" title={[info?.name, info?.description].filter(Boolean).join(": ") || undefined}>
            Viewing snapshot <strong className="font-semibold">{info?.name ?? scope}</strong>
            {info ? ` (${snapshotTime(info.createdUtc)})` : ""}
          </span>
          <span className="shrink-0">— read-only</span>
        </>
      )}
      {info ? (
        <>
          <Button size="sm" variant="ghost" onClick={() => openCompare(scope, WORKING)} data-testid="as-of-compare">
            Compare with working
          </Button>
          <span title={restoreDenied ?? undefined} className="inline-flex">
            <Button
              size="sm"
              variant="ghost"
              disabled={!!restoreDenied}
              onClick={() => openSnapshotDialog({ kind: "restore", snapshot: info })}
              data-testid="as-of-restore"
            >
              Restore…
            </Button>
          </span>
        </>
      ) : null}
      <Button size="sm" variant="primary" onClick={back} data-testid="as-of-back">
        Back to working
      </Button>
    </div>
  );
}
