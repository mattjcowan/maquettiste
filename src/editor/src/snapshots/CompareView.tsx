// The compare view (docs/engineering/snapshots.md section 5): a full-height panel over the screen. On the left the changed
// elements by kind with counts (added, removed, changed; a rename says so), filtered by kind, change and name, in pages of
// 500 (the next page on request); the other changed documents by path on their own tab. On the right the element picked:
// its fields that differ and both documents side by side in the conflict dialog's diff view. Each comparison and each
// element is read when asked for, never ahead.
import { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { Badge, EmptyState, Spinner } from "@/components/ui/misc";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { CodeDiff } from "@/code";
import type { SnapshotComparison, SnapshotElementDiff } from "@/api/types";
import { cn } from "@/lib/cn";
import { useComparison, useElementDiff, useSnapshots } from "./queries";
import { closeCompare, openCompare, sideLabel, snapshotTime, useSnapshotUi, WORKING } from "./state";

type Change = SnapshotComparison["elements"][number];
type ChangeFilter = "all" | "added" | "removed" | "changed" | "renamed";

const MARK = { added: "+", removed: "−", changed: "~" } as const;
const TONE = { added: "text-success", removed: "text-danger", changed: "text-warning" } as const;

export function CompareView() {
  const compare = useSnapshotUi((s) => s.compare);
  if (!compare) return null;
  return <CompareBody key={`${compare.from}>${compare.to}`} from={compare.from} to={compare.to} />;
}

/** The rows that pass the filters: kind, change (a rename is a change with a previous name) and a name or id search. */
export function filterChanges(rows: readonly Change[], kind: string, change: ChangeFilter, text: string): Change[] {
  const needle = text.trim().toLowerCase();
  return rows.filter(
    (r) =>
      (kind === "" || r.kind === kind) &&
      (change === "all" || (change === "renamed" ? r.previousName !== undefined : r.change === change)) &&
      (!needle || r.name.toLowerCase().includes(needle) || (r.previousName ?? "").toLowerCase().includes(needle) || r.id.toLowerCase().includes(needle)),
  );
}

function CompareBody({ from, to }: { from: string; to: string }) {
  const snapshots = useSnapshots();
  const comparison = useComparison(from, to);
  const [kind, setKind] = useState("");
  const [change, setChange] = useState<ChangeFilter>("all");
  const [text, setText] = useState("");
  const [picked, setPicked] = useState<string | null>(null);
  const first = comparison.data?.pages[0];
  const loaded = useMemo(() => comparison.data?.pages.flatMap((p) => p.elements) ?? [], [comparison.data]);
  const rows = useMemo(() => filterChanges(loaded, kind, change, text), [loaded, kind, change, text]);
  const total = first ? first.added + first.removed + first.changed : 0;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !e.defaultPrevented) closeCompare();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const sides = [
    { id: WORKING, label: "Working model" },
    ...(snapshots.data ?? []).map((s) => ({ id: s.id, label: `${s.name} (${snapshotTime(s.createdUtc)})` })),
  ];

  return (
    <section
      role="dialog"
      aria-modal="false"
      aria-label="Compare snapshots"
      className="fixed inset-x-0 bottom-0 top-[var(--mq-topbar-h)] z-40 flex flex-col border-t border-default bg-surface text-12"
      data-testid="compare-view"
    >
      <header className="flex h-8 shrink-0 items-center gap-2 border-b border-default px-2">
        <h2 className="text-13 font-semibold">Compare</h2>
        <Select aria-label="From" className="h-6 w-56 text-12" value={from} onChange={(e) => openCompare(e.target.value, to)} data-testid="compare-from">
          {sides.map((s) => (
            <option key={s.id} value={s.id}>
              {s.label}
            </option>
          ))}
        </Select>
        <span aria-hidden>→</span>
        <Select aria-label="To" className="h-6 w-56 text-12" value={to} onChange={(e) => openCompare(from, e.target.value)} data-testid="compare-to">
          {sides.map((s) => (
            <option key={s.id} value={s.id}>
              {s.label}
            </option>
          ))}
        </Select>
        {first ? (
          <span className="flex items-center gap-2" data-testid="compare-totals">
            <span className="text-success">+{first.added} added</span>
            <span className="text-danger">−{first.removed} removed</span>
            <span className="text-warning">~{first.changed} changed</span>
            {first.files.length ? <span className="text-secondary">· {first.files.length} other documents</span> : null}
          </span>
        ) : null}
        <span className="flex-1" />
        <Button size="icon-sm" variant="ghost" label="Close the comparison" shortcut="Esc" onClick={closeCompare} data-testid="compare-close">
          <X />
        </Button>
      </header>
      {comparison.isPending ? (
        <div className="flex flex-1 items-center justify-center">
          <Spinner label={`Comparing ${sideLabel(from, snapshots.data)} with ${sideLabel(to, snapshots.data)}`} />
        </div>
      ) : comparison.isError ? (
        <EmptyState title="The comparison failed">{(comparison.error as Error).message}</EmptyState>
      ) : first && total === 0 && first.files.length === 0 ? (
        <EmptyState title="No differences">Both sides hold the same documents.</EmptyState>
      ) : first ? (
        <div className="flex min-h-0 flex-1">
          <Tabs defaultValue="elements" className="flex w-[420px] shrink-0 flex-col border-r border-default">
            <TabsList aria-label="What changed">
              <TabsTrigger value="elements">Elements ({total.toLocaleString()})</TabsTrigger>
              <TabsTrigger value="files">
                Other documents ({first.files.length}
                {first.filesTruncated ? "+" : ""})
              </TabsTrigger>
            </TabsList>
            <TabsContent value="elements" className="flex min-h-0 flex-1 flex-col">
              <KindCounts comparison={first} kind={kind} onKind={setKind} />
              <div className="flex shrink-0 items-center gap-1 border-b border-default p-1">
                <Select
                  aria-label="Change"
                  className="h-6 w-28 text-12"
                  value={change}
                  onChange={(e) => setChange(e.target.value as ChangeFilter)}
                  data-testid="compare-change"
                >
                  <option value="all">All changes</option>
                  <option value="added">Added</option>
                  <option value="removed">Removed</option>
                  <option value="changed">Changed</option>
                  <option value="renamed">Renamed</option>
                </Select>
                <Input
                  type="search"
                  aria-label="Find an element by name"
                  placeholder="Find by name"
                  className="h-6 flex-1 text-12"
                  value={text}
                  onChange={(e) => setText(e.target.value)}
                  data-testid="compare-search"
                />
              </div>
              <ChangeList rows={rows} picked={picked} onPick={setPicked} />
              <div className="flex h-7 shrink-0 items-center gap-2 border-t border-default px-2 text-secondary">
                <span data-testid="compare-loaded">
                  {rows.length.toLocaleString()} shown · {loaded.length.toLocaleString()} of {total.toLocaleString()} loaded
                </span>
                {comparison.hasNextPage ? (
                  <Button size="sm" disabled={comparison.isFetchingNextPage} onClick={() => void comparison.fetchNextPage()} data-testid="compare-more">
                    {comparison.isFetchingNextPage ? "Loading…" : "Load more"}
                  </Button>
                ) : null}
              </div>
            </TabsContent>
            <TabsContent value="files" className="mq-scroll min-h-0 flex-1 overflow-y-auto">
              <ul aria-label="Other changed documents" data-testid="compare-files">
                {first.files.map((f) => (
                  <li key={f.path} className="flex h-6 items-center gap-2 px-2 font-mono">
                    <span className={cn("w-3 shrink-0 text-center", TONE[f.change])} aria-label={f.change}>
                      {MARK[f.change]}
                    </span>
                    <span className="truncate" title={f.path}>
                      {f.path}
                    </span>
                  </li>
                ))}
                {first.files.length === 0 ? <li className="p-2 text-secondary">Settings, translations, extensions and branding are the same.</li> : null}
                {first.filesTruncated ? <li className="p-2 text-secondary">The first 1,000 are listed.</li> : null}
              </ul>
              {!first.packsCompared ? <p className="p-2 text-secondary">The template packs are compared only when both sides hold them.</p> : null}
            </TabsContent>
          </Tabs>
          <div className="flex min-w-0 flex-1 flex-col">
            {picked ? (
              <ElementDetail from={from} to={to} id={picked} />
            ) : (
              <EmptyState title="Pick an element">Its fields that differ and both documents side by side are read when you pick it.</EmptyState>
            )}
          </div>
        </div>
      ) : null}
    </section>
  );
}

function KindCounts({ comparison, kind, onKind }: { comparison: SnapshotComparison; kind: string; onKind: (kind: string) => void }) {
  return (
    <ul
      className="mq-scroll flex max-h-32 shrink-0 flex-col overflow-y-auto border-b border-default py-1"
      aria-label="Changes by kind"
      data-testid="compare-kinds"
    >
      <li>
        <button
          type="button"
          className={cn("flex h-5 w-full items-center gap-2 px-2 text-left hover:bg-accent-subtle", kind === "" && "bg-accent-subtle")}
          onClick={() => onKind("")}
        >
          <span className="flex-1 font-medium">All kinds</span>
        </button>
      </li>
      {comparison.kinds.map((k) => (
        <li key={k.kind}>
          <button
            type="button"
            className={cn("flex h-5 w-full items-center gap-2 px-2 text-left hover:bg-accent-subtle", kind === k.kind && "bg-accent-subtle")}
            onClick={() => onKind(kind === k.kind ? "" : k.kind)}
            aria-pressed={kind === k.kind}
            data-testid={`compare-kind-${k.kind}`}
          >
            <span className="flex-1">{k.kind}</span>
            {k.added ? <span className="text-success">+{k.added}</span> : null}
            {k.removed ? <span className="text-danger">−{k.removed}</span> : null}
            {k.changed ? <span className="text-warning">~{k.changed}</span> : null}
          </button>
        </li>
      ))}
    </ul>
  );
}

function ChangeList({ rows, picked, onPick }: { rows: Change[]; picked: string | null; onPick: (id: string) => void }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => 24, overscan: 20 });
  if (rows.length === 0) return <p className="flex-1 p-2 text-secondary">No loaded change matches.</p>;
  return (
    <div ref={scrollRef} className="mq-scroll min-h-0 flex-1 overflow-y-auto" role="listbox" aria-label="Changed elements" data-testid="compare-elements">
      <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
        {virtualizer.getVirtualItems().map((item) => {
          const r = rows[item.index];
          return (
            <div
              key={r.id}
              role="option"
              aria-selected={picked === r.id}
              tabIndex={0}
              style={{ position: "absolute", top: 0, left: 0, right: 0, height: item.size, transform: `translateY(${item.start}px)` }}
              className={cn("flex cursor-default items-center gap-2 px-2 hover:bg-accent-subtle", picked === r.id && "bg-accent-subtle")}
              onClick={() => onPick(r.id)}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  onPick(r.id);
                }
              }}
              data-testid="compare-element"
              data-change={r.change}
            >
              <span className={cn("w-3 shrink-0 text-center font-mono", TONE[r.change])} aria-label={r.change}>
                {MARK[r.change]}
              </span>
              <span className="min-w-0 flex-1 truncate">
                {r.name || r.id}
                {r.previousName !== undefined ? <span className="text-secondary"> (renamed from {r.previousName || "no name"})</span> : null}
              </span>
              <span className="shrink-0 text-11 text-secondary">{r.kind}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

const short = (value: unknown) => {
  if (value === null || value === undefined) return "—";
  const text = typeof value === "string" ? JSON.stringify(value) : JSON.stringify(value);
  return text.length > 120 ? `${text.slice(0, 117)}…` : text;
};

function ElementDetail({ from, to, id }: { from: string; to: string; id: string }) {
  const diff = useElementDiff(from, to, id);
  if (diff.isPending) return <Spinner label="Reading both documents" />;
  if (diff.isError) return <EmptyState title="The element could not be read">{(diff.error as Error).message}</EmptyState>;
  return <ElementDiffView diff={diff.data} />;
}

function ElementDiffView({ diff }: { diff: SnapshotElementDiff }) {
  const before = diff.before ? JSON.stringify(diff.before, null, 2) : "";
  const after = diff.after ? JSON.stringify(diff.after, null, 2) : "";
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="compare-detail">
      <div className="flex h-7 shrink-0 items-center gap-2 border-b border-default px-2">
        <span className="truncate font-semibold">{diff.name || diff.id}</span>
        <Badge>{diff.kind}</Badge>
        <Badge tone={diff.change === "added" ? "success" : diff.change === "removed" ? "danger" : "warning"}>{diff.change}</Badge>
        <span className="min-w-0 flex-1 truncate font-mono text-11 text-secondary" title={`${diff.fromPath ?? "—"} → ${diff.toPath ?? "—"}`}>
          {diff.fromPath === diff.toPath ? diff.toPath : `${diff.fromPath ?? "—"} → ${diff.toPath ?? "—"}`}
        </span>
      </div>
      {diff.fields.length ? (
        <table className="w-full shrink-0 border-b border-default text-12" data-testid="compare-fields">
          <caption className="sr-only">Fields that differ</caption>
          <thead className="text-left text-11 text-secondary">
            <tr>
              <th className="px-2 font-medium">Field</th>
              <th className="px-2 font-medium">Before</th>
              <th className="px-2 font-medium">After</th>
            </tr>
          </thead>
          <tbody>
            {diff.fields.slice(0, 50).map((f) => (
              <tr key={f.pointer} className="align-top">
                <td className="px-2 font-mono">
                  <span className={TONE[f.change]}>{MARK[f.change]}</span> {f.pointer || "(document)"}
                </td>
                <td className="max-w-0 truncate px-2 font-mono text-secondary" title={short(f.before)}>
                  {f.change === "added" ? "—" : short(f.before)}
                </td>
                <td className="max-w-0 truncate px-2 font-mono" title={short(f.after)}>
                  {f.change === "removed" ? "—" : short(f.after)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      {diff.fields.length > 50 || diff.fieldsTruncated ? (
        <p className="shrink-0 border-b border-default px-2 py-1 text-secondary">
          {diff.fieldsTruncated ? "Over 500 fields differ; " : ""}the documents below show every difference.
        </p>
      ) : null}
      <div className="min-h-0 flex-1" data-testid="compare-diff">
        <CodeDiff label={`${diff.name || diff.id}: before and after`} original={before} modified={after} readOnly />
      </div>
    </div>
  );
}
