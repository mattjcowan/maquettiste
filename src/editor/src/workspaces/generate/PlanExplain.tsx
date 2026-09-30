// The plan explained (generation-ui.md 4): the summary line per pack, the planned changes grouped by unit with counts
// and filters, the selected file's explanation (pack, unit, template, element, output path, reason, causes), the
// unchanged units with "Why not?" (GET /api/generate/plan/{id}/unit) and Explain for any unit and element
// (POST /api/generate/explain). One density: 24 px rows, 12 px text.
import { ROW_H } from "@/design/density";
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, TriangleAlert } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, useIndex, usePacks } from "@/api/queries";
import { useEditorNavigation } from "@/app/navigation";
import type { ExplainResult, FileChangeKind, GenerationPlan, PlanUnit } from "@/api/types";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/misc";
import { Input, Select } from "@/components/ui/input";
import { cn } from "@/lib/cn";
import {
  causeGroups,
  causeSentence,
  countsText,
  filterGroups,
  flattenGroups,
  groupPlan,
  planSummary,
  rootGroups,
  type CauseLink,
  type PlanFilter,
  type PlanRow,
  type UnitGroup,
} from "./planModel";
import { openPackTab } from "./packTabs";

export const KIND_TONE: Partial<Record<FileChangeKind, "success" | "danger" | "warning" | "accent" | "neutral">> = {
  added: "success",
  modified: "accent",
  deleted: "danger",
  "hand-edited": "warning",
  conflict: "danger",
  "orphaned-owned": "warning",
};

/** A unit the `e` key (or the Why panel) asks the Explain form to explain. */
export interface ExplainAsk {
  pack: string;
  unit: string;
  elementId: string | null;
  seq: number;
}

const COLS = "grid grid-cols-[88px_minmax(0,1.3fr)_minmax(0,128px)_minmax(0,120px)_minmax(0,1fr)] items-center gap-2";

/** The summary lines: "sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete". */
export function PlanSummary({ plan }: { plan: GenerationPlan }) {
  const lines = useMemo(() => planSummary(plan), [plan]);
  const packs = usePacks();
  const causes = useMemo(() => causeGroups(plan), [plan]);
  const roots = useMemo(
    () =>
      rootGroups(
        plan,
        (packs.data ?? []).map((p) => p.output),
      ),
    [plan, packs.data],
  );
  const [allCauses, setAllCauses] = useState(false);
  const { store } = useServices();
  const { goTo, openSettings } = useEditorNavigation();
  const follow = (link: CauseLink) => {
    const g = store.getState().generation;
    if (link.type === "element") goTo(link.id);
    else if (link.type === "setting") openSettings(link.tab);
    else if (link.type === "template") store.getState().setGeneration(openPackTab(g, link.pack, "templates", { file: link.path }));
    else if (link.type === "parameter") store.getState().setGeneration(openPackTab(g, link.pack, "parameters", { parameter: link.name }));
    else store.getState().setGeneration(openPackTab(g, link.pack, "units", { unit: link.unit }));
  };
  const shown = allCauses ? causes : causes.slice(0, 8);
  return (
    <div className="flex flex-col gap-1">
      <ul className="flex flex-col text-12" data-testid="plan-summary">
        {lines.map((line) => (
          <li key={line} className="leading-5">
            {line}
          </li>
        ))}
      </ul>
      {causes.length ? (
        <section aria-label="Files by cause" data-testid="plan-causes">
          <h4 className="text-11 font-semibold text-secondary">By cause</h4>
          <ul className="flex flex-col text-12">
            {shown.map((g) => (
              <li key={g.id} className="leading-5" data-testid="plan-cause">
                {g.link ? (
                  <button
                    type="button"
                    className="text-left text-accent hover:underline"
                    onClick={() => follow(g.link!)}
                    title={`Open what this cause names (${g.kind})`}
                  >
                    {g.detail}
                  </button>
                ) : (
                  g.detail
                )}
                <span className="text-secondary">: {causeSentence(g).slice(g.detail.length + 2)}</span>
              </li>
            ))}
          </ul>
          {causes.length > shown.length || allCauses ? (
            <Button size="sm" variant="ghost" onClick={() => setAllCauses(!allCauses)}>
              {allCauses ? "Fewer causes" : `${causes.length - shown.length} more causes`}
            </Button>
          ) : null}
        </section>
      ) : null}
      {roots.length ? (
        <section aria-label="Files by output root" data-testid="plan-roots">
          <h4 className="text-11 font-semibold text-secondary">By output root</h4>
          <ul className="flex flex-col text-12">
            {roots.map((r) => (
              <li key={r.root} className="leading-5" data-testid="plan-root">
                <span className="font-mono">{r.root}</span>
                <span className="text-secondary">
                  : {r.files} {r.files === 1 ? "file" : "files"} ({countsText(r.counts)})
                </span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}

function useElementNames(): Map<string, string> {
  const index = useIndex();
  return useMemo(() => new Map((index.data ?? []).map((e) => [e.id, e.name])), [index.data]);
}

function elementLabel(names: Map<string, string>, id: string | null | undefined): string {
  if (!id) return "(model)";
  return names.get(id) ?? id;
}

/** The planned changes grouped by unit, filterable; a row selects its diff and its explanation. */
export function PlanChanges({ planId, plan, onExplain }: { planId: string; plan: GenerationPlan; onExplain?: (ask: Omit<ExplainAsk, "seq">) => void }) {
  const { store } = useServices();
  const diff = useEditor(store, (s) => s.diff);
  const names = useElementNames();
  const [filter, setFilter] = useState<PlanFilter>({ kind: "changed", pack: "", unit: "", text: "" });
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  const groups = useMemo(() => groupPlan(plan), [plan]);
  const shown = useMemo(() => filterGroups(groups, filter), [groups, filter]);
  const items = useMemo(() => flattenGroups(shown, collapsed), [shown, collapsed]);
  const files = shown.reduce((n, g) => n + g.rows.length, 0);
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({ count: items.length, getScrollElement: () => scrollRef.current, estimateSize: () => ROW_H, overscan: 20 });
  // One Tab stop (roving tabindex): arrows, Home, End, PageUp and PageDown move the focused row through the
  // virtualizer; Left and Right fold a group; Enter opens the diff; Space expands a file's causes; `e` explains.
  const [focusAt, setFocusAt] = useState(0);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set());
  const at = Math.min(focusAt, Math.max(0, items.length - 1));
  const focusRow = (i: number) => {
    const to = Math.max(0, Math.min(items.length - 1, i));
    setFocusAt(to);
    virtualizer.scrollToIndex(to, { align: "auto" });
    requestAnimationFrame(() => scrollRef.current?.querySelector<HTMLElement>(`[data-row="${to}"]`)?.focus());
  };
  const onGridKey = (e: React.KeyboardEvent) => {
    const item = items[at];
    if (!item) return;
    const page = Math.max(1, Math.floor((scrollRef.current?.clientHeight ?? ROW_H * 10) / ROW_H) - 1);
    const move: Record<string, number> = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: items.length - 1, PageDown: at + page, PageUp: at - page };
    if (e.key in move) {
      e.preventDefault();
      focusRow(move[e.key]);
      return;
    }
    if (item.type === "group") {
      if (e.key === "Enter" || e.key === " " || (e.key === "ArrowRight" && item.collapsed) || (e.key === "ArrowLeft" && !item.collapsed)) {
        e.preventDefault();
        toggle(item.group.id);
      } else if (e.key === "e" && onExplain) {
        e.preventDefault();
        onExplain({ pack: item.group.pack, unit: item.group.unit, elementId: null });
      }
      return;
    }
    const c = item.row.change;
    if (e.key === "Enter") {
      e.preventDefault();
      store.getState().showDiff({ planId, path: c.path });
    } else if (e.key === " ") {
      e.preventDefault();
      setExpanded((x) => {
        const next = new Set(x);
        if (!next.delete(c.path)) next.add(c.path);
        return next;
      });
    } else if (e.key === "ArrowLeft") {
      e.preventDefault();
      const parent = items.findLastIndex((it, i) => i < at && it.type === "group");
      if (parent >= 0) focusRow(parent);
    } else if (e.key === "e" && onExplain && item.row.unit) {
      e.preventDefault();
      onExplain({ pack: item.row.unit.pack ?? "", unit: item.row.unit.unit ?? "", elementId: item.row.unit.elementId ?? null });
    }
  };
  const kinds = [...new Set(plan.changes.map((c) => c.kind))].sort();
  const packs = [...new Set(groups.map((g) => g.pack))].sort();
  const unitIds = groups.filter((g) => !filter.pack || g.pack === filter.pack).map((g) => g.id);
  const set = (patch: Partial<PlanFilter>) => setFilter((f) => ({ ...f, ...patch }));
  const toggle = (id: string) =>
    setCollapsed((c) => {
      const next = new Set(c);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  return (
    <div className="flex min-h-0 flex-1 flex-col gap-1">
      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="filter-kind" className="text-12 text-secondary">
          Show
        </label>
        <Select id="filter-kind" className="h-6 w-36 text-12" value={filter.kind} onChange={(e) => set({ kind: e.target.value })}>
          <option value="changed">All but unchanged</option>
          <option value="">Everything</option>
          {kinds.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </Select>
        <label htmlFor="filter-pack" className="text-12 text-secondary">
          Pack
        </label>
        <Select id="filter-pack" className="h-6 w-32 text-12" value={filter.pack} onChange={(e) => set({ pack: e.target.value, unit: "" })}>
          <option value="">All packs</option>
          {packs.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </Select>
        <label htmlFor="filter-unit" className="text-12 text-secondary">
          Unit
        </label>
        <Select id="filter-unit" className="h-6 w-40 text-12" value={filter.unit} onChange={(e) => set({ unit: e.target.value })}>
          <option value="">All units</option>
          {unitIds.map((u) => (
            <option key={u} value={u}>
              {u}
            </option>
          ))}
        </Select>
        <Input
          aria-label="Filter changes by path, element, template or reason"
          placeholder="Filter path, element, why…"
          className="h-6 w-48 text-12"
          value={filter.text}
          onChange={(e) => set({ text: e.target.value })}
          data-testid="filter-text"
        />
        <span className="ml-auto text-12 text-secondary" data-testid="changes-count">
          {files} files in {shown.length} units
        </span>
      </div>
      <div className="min-h-0 flex-1 overflow-hidden rounded-control border border-default">
        <div role="treegrid" aria-label="Planned file changes" aria-rowcount={items.length + 1} className="flex h-full flex-col text-12" onKeyDown={onGridKey}>
          <div role="rowgroup">
            <div role="row" aria-rowindex={1} className={cn(COLS, "bg-app px-2")}>
              {["Change", "File", "Unit", "Element", "Why"].map((h) => (
                <div role="columnheader" key={h} className="py-0.5 text-11 font-semibold text-secondary">
                  {h}
                </div>
              ))}
            </div>
          </div>
          <div ref={scrollRef} role="rowgroup" className="min-h-0 flex-1 overflow-auto" data-testid="changes">
            <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
              {virtualizer.getVirtualItems().map((v) => {
                const item = items[v.index];
                const style = { position: "absolute", top: 0, left: 0, right: 0, minHeight: ROW_H, transform: `translateY(${v.start}px)` } as const;
                const nav = {
                  "data-row": v.index,
                  "data-index": v.index,
                  ref: virtualizer.measureElement,
                  "aria-rowindex": v.index + 2,
                  tabIndex: v.index === at ? 0 : -1,
                  onFocus: () => setFocusAt(v.index),
                };
                if (item.type === "group")
                  return <GroupRow key={`g:${item.group.id}`} group={item.group} collapsed={item.collapsed} style={style} onToggle={toggle} nav={nav} />;
                const c = item.row.change;
                const active = diff?.planId === planId && diff.path === c.path;
                const open = expanded.has(c.path);
                const causes = item.row.unit?.causes ?? [];
                return (
                  <div
                    role="row"
                    {...nav}
                    aria-level={2}
                    aria-expanded={causes.length > 1 ? open : undefined}
                    key={`r:${c.path}`}
                    style={style}
                    className={cn(
                      COLS,
                      "cursor-default pl-6 pr-2 text-left hover:bg-accent-subtle focus-visible:outline-2 focus-visible:outline-accent",
                      active && "bg-accent-subtle",
                    )}
                    onClick={() => store.getState().showDiff({ planId, path: c.path })}
                    aria-selected={active}
                    data-testid={`change-${c.path}`}
                  >
                    <span role="gridcell" className="flex items-center gap-1">
                      {c.kind === "hand-edited" || c.kind === "conflict" ? (
                        <TriangleAlert className="size-3.5 text-warning" aria-label="needs attention" />
                      ) : null}
                      <Badge tone={KIND_TONE[c.kind] ?? "neutral"}>{c.kind}</Badge>
                    </span>
                    <span role="gridcell" className="truncate font-mono" title={c.path}>
                      {c.path}
                    </span>
                    <span role="gridcell" className="truncate text-secondary" title={item.row.unit?.template ?? undefined}>
                      {item.row.group}
                    </span>
                    <span role="gridcell" className="truncate" title={item.row.unit?.elementId ?? undefined}>
                      {item.row.unit ? elementLabel(names, item.row.unit.elementId) : "—"}
                    </span>
                    <span role="gridcell" className="truncate text-secondary" title={item.row.why}>
                      {open && causes.length ? (
                        <ul className="whitespace-normal py-0.5" data-testid={`causes-${c.path}`}>
                          {causes.map((k, n) => (
                            <li key={n}>{k.detail}</li>
                          ))}
                        </ul>
                      ) : (
                        item.row.why
                      )}
                    </span>
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function GroupRow({
  group,
  collapsed,
  style,
  onToggle,
  nav,
}: {
  group: UnitGroup;
  collapsed: boolean;
  style: React.CSSProperties;
  onToggle: (id: string) => void;
  nav: Record<string, unknown>;
}) {
  const Chevron = collapsed ? ChevronRight : ChevronDown;
  return (
    <div
      role="row"
      {...nav}
      aria-level={1}
      aria-expanded={!collapsed}
      style={style}
      className="flex cursor-default items-center gap-2 border-t border-default bg-surface px-2 text-left hover:bg-accent-subtle focus-visible:outline-2 focus-visible:outline-accent"
      onClick={() => onToggle(group.id)}
      data-testid={`unit-group-${group.id}`}
    >
      <span role="gridcell" className="flex min-w-0 flex-1 items-center gap-2">
        <Chevron className="size-3.5 shrink-0 text-secondary" aria-hidden />
        <span className="font-semibold">{group.id}</span>
        {group.template ? <span className="truncate font-mono text-11 text-secondary">{group.template}</span> : null}
        <span className="text-11 text-secondary">
          {group.rows.length} files: {countsText(group.counts)}
        </span>
        <span className="ml-auto shrink-0 text-11 text-secondary">
          {group.rendering} rendering{group.skipped ? `, ${group.skipped} unchanged` : ""}
        </span>
      </span>
    </div>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-secondary">{label}</dt>
      <dd className="min-w-0 truncate">{children}</dd>
    </>
  );
}

/** The API's answer for one unit of the plan: its summary and its recorded read keys by kind. */
function UnitAnswer({ planId, unitKey }: { planId: string; unitKey: string }) {
  const answer = useQuery({ queryKey: [...keys.plan(planId), "unit", unitKey], queryFn: () => endpoints.getPlanUnit(planId, unitKey) });
  if (answer.isPending) return <p className="text-11 text-secondary">Asking…</p>;
  if (answer.isError) return <p className="text-11 text-danger">{answer.error.message}</p>;
  return (
    <div className="flex flex-col gap-0.5 text-11" data-testid="why-not-answer" role="status">
      <p>{answer.data.summary}</p>
      <ul className="text-secondary">
        {answer.data.groups.map((g) => (
          <li key={g.kind} className="truncate font-mono" title={g.keys.join(" ")}>
            {g.kind} ({g.keys.length}): {g.keys.slice(0, 4).join(" ")}
            {g.keys.length > 4 ? " …" : ""}
          </li>
        ))}
      </ul>
    </div>
  );
}

function causeList(unit: PlanUnit) {
  return (
    <ul className="flex flex-col" data-testid="why-causes">
      {unit.causes.map((c) => (
        <li key={`${c.kind}:${c.key}`} className="truncate" title={c.key}>
          <span className="text-secondary">{c.kind}</span> {c.detail}
        </li>
      ))}
      {unit.causeCount > unit.causes.length ? <li className="text-secondary">and {unit.causeCount - unit.causes.length} more</li> : null}
    </ul>
  );
}

/** The selected file's explanation: where it comes from and why its unit renders. */
export function WhyPanel({ planId, plan }: { planId: string; plan: GenerationPlan }) {
  const { store } = useServices();
  const diff = useEditor(store, (s) => s.diff);
  const names = useElementNames();
  const [ask, setAsk] = useState<string | null>(null);
  const row: PlanRow | undefined = useMemo(() => {
    if (!diff || diff.planId !== planId) return undefined;
    for (const g of groupPlan(plan)) for (const r of g.rows) if (r.change.path === diff.path) return r;
    return undefined;
  }, [diff, plan, planId]);
  return (
    <section className="flex flex-col gap-1 border-b border-default px-2 py-1 text-12" aria-label="Why this file" data-testid="why-panel">
      <h3 className="text-12 font-semibold">Why this file</h3>
      {!row ? (
        <p className="text-11 text-secondary">Select a planned file to see its pack, unit, template, element and why it renders.</p>
      ) : (
        <>
          <dl className="grid grid-cols-[64px_minmax(0,1fr)] gap-x-2 text-12">
            <Fact label="Pack">{row.change.pack}</Fact>
            <Fact label="Unit">{row.unit?.unit ?? row.group.split("/")[1]}</Fact>
            <Fact label="Template">
              <span className="font-mono">{row.unit?.template ?? "—"}</span>
            </Fact>
            <Fact label="Element">{row.unit ? elementLabel(names, row.unit.elementId) : "—"}</Fact>
            <Fact label="Output">
              <span className="font-mono" title={row.change.path}>
                {row.change.path}
              </span>
            </Fact>
            <Fact label="Change">{row.change.kind}</Fact>
            <Fact label="Reason">{row.unit?.reason ?? "orphan"}</Fact>
          </dl>
          <p className="text-12">{row.why}</p>
          {row.unit && row.unit.causes.length ? causeList(row.unit) : null}
          {row.unit ? (
            ask === row.unit.key ? (
              <UnitAnswer planId={planId} unitKey={row.unit.key} />
            ) : (
              <Button size="sm" className="h-6 self-start" onClick={() => setAsk(row.unit!.key)} data-testid="why-read-keys">
                What it read
              </Button>
            )
          ) : null}
        </>
      )}
    </section>
  );
}

/** The units the plan skips, each with "Why not?". */
export function UnchangedUnits({ planId, plan }: { planId: string; plan: GenerationPlan }) {
  const names = useElementNames();
  const [open, setOpen] = useState(false);
  const [asked, setAsked] = useState<ReadonlySet<string>>(new Set());
  const [text, setText] = useState("");
  const skipped = useMemo(() => plan.units.filter((u) => u.skipped), [plan]);
  const words = text.toLowerCase().split(/\s+/).filter(Boolean);
  const shown = skipped.filter((u) => words.every((w) => `${u.key} ${elementLabel(names, u.elementId)}`.toLowerCase().includes(w))).slice(0, 200);
  return (
    <section className="flex flex-col gap-1 border-b border-default px-2 py-1 text-12" aria-label="Unchanged units" data-testid="unchanged-units">
      <button type="button" className="flex h-6 items-center gap-1 text-left font-semibold" aria-expanded={open} onClick={() => setOpen(!open)}>
        {open ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
        Unchanged units ({skipped.length})
      </button>
      {open ? (
        skipped.length === 0 ? (
          <p className="text-11 text-secondary">Every unit in this plan renders.</p>
        ) : (
          <>
            <Input
              aria-label="Filter unchanged units"
              placeholder="Filter units…"
              className="h-6 text-12"
              value={text}
              onChange={(e) => setText(e.target.value)}
            />
            <ul className="flex max-h-72 flex-col overflow-auto">
              {shown.map((u) => (
                <li key={u.key} className="flex flex-col border-t border-default py-0.5" data-testid={`unchanged-${u.key}`}>
                  <div className="flex h-6 items-center gap-2">
                    <span className="min-w-0 flex-1 truncate" title={u.key}>
                      {u.pack}/{u.unit} <span className="text-secondary">{elementLabel(names, u.elementId)}</span>
                    </span>
                    <Button size="sm" className="h-6 px-2" onClick={() => setAsked((a) => new Set(a).add(u.key))} disabled={asked.has(u.key)}>
                      Why not?
                    </Button>
                  </div>
                  {asked.has(u.key) ? <UnitAnswer planId={planId} unitKey={u.key} /> : null}
                </li>
              ))}
            </ul>
            {skipped.length > shown.length && !words.length ? <p className="text-11 text-secondary">Showing 200; filter to find the others.</p> : null}
          </>
        )
      ) : null}
    </section>
  );
}

/** Explain any unit for any element, planned or not (POST /api/generate/explain). */
export function ExplainForm({ planId, plan, ask }: { planId: string | null; plan: GenerationPlan | null; ask?: ExplainAsk | null }) {
  const packs = useMemo(() => [...new Set(plan?.units.map((u) => u.pack ?? "").filter(Boolean) ?? [])].sort(), [plan]);
  const [pack, setPack] = useState("");
  const [unit, setUnit] = useState("");
  const [element, setElement] = useState("");
  const [answer, setAnswer] = useState<ExplainResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const chosen = pack || packs[0] || "";
  const units = useMemo(() => [...new Set(plan?.units.filter((u) => u.pack === chosen).map((u) => u.unit ?? "") ?? [])].filter(Boolean).sort(), [plan, chosen]);
  const run = async (q?: { pack: string; unit: string; elementId: string | null }) => {
    setError(null);
    try {
      const r = q ?? { pack: chosen, unit, elementId: element.trim() || null };
      setAnswer(await endpoints.explainUnit({ ...r, planId, packs: plan?.packs ?? null }));
    } catch (e) {
      setAnswer(null);
      setError((e as Error).message);
    }
  };
  // The `e` key in the changes table fills the form and asks at once.
  useEffect(() => {
    if (!ask) return;
    setPack(ask.pack);
    setUnit(ask.unit);
    setElement(ask.elementId ?? "");
    void run(ask);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ask?.seq]);
  return (
    <section className="flex flex-col gap-1 border-b border-default px-2 py-1 text-12" aria-label="Explain a unit" data-testid="explain">
      <h3 className="text-12 font-semibold">Explain</h3>
      <div className="grid grid-cols-[48px_minmax(0,1fr)] items-center gap-1">
        <label htmlFor="explain-pack" className="text-secondary">
          Pack
        </label>
        <Select id="explain-pack" className="h-6 text-12" value={chosen} onChange={(e) => setPack(e.target.value)}>
          {packs.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </Select>
        <label htmlFor="explain-unit" className="text-secondary">
          Unit
        </label>
        <Input id="explain-unit" list="explain-units" className="h-6 text-12" value={unit} onChange={(e) => setUnit(e.target.value)} />
        <datalist id="explain-units">
          {units.map((u) => (
            <option key={u} value={u} />
          ))}
        </datalist>
        <label htmlFor="explain-element" className="text-secondary">
          Element
        </label>
        <Input
          id="explain-element"
          placeholder="id, or empty for the model"
          className="h-6 text-12"
          value={element}
          onChange={(e) => setElement(e.target.value)}
        />
      </div>
      <Button size="sm" className="h-6 self-start" disabled={!chosen || !unit.trim()} onClick={() => void run()} data-testid="explain-run">
        Explain
      </Button>
      {error ? <p className="text-11 text-danger">{error}</p> : null}
      {answer ? (
        <div className="flex flex-col gap-0.5" role="status" data-testid="explain-answer">
          <span>
            <Badge tone={answer.planned ? "accent" : "neutral"}>{answer.reason}</Badge> <span className="font-mono text-11">{answer.key}</span>
          </span>
          <p>{answer.detail}</p>
          {answer.planUnit?.causes.length ? causeList(answer.planUnit) : null}
        </div>
      ) : null}
    </section>
  );
}
