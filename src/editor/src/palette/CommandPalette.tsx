// The command palette (Ctrl/Cmd+K) and quick open (Ctrl/Cmd+P), explorer-redesign.md 3.1: one ranked flat list
// over every kind from the search worker (elements, reference types, tables, diagrams), with the screens, and in the
// palette the shell's commands. `>` keeps to the commands. The search syntax is the explorer box's (search/query.ts):
// typing an operator switches from ranking to the operator's plain match. cmdk on a Radix dialog, filtering off: the
// worker and search/rank.ts decide the order.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Command } from "cmdk";
import { useIndex, useProject } from "@/api/queries";
import type { ElementKind, ElementSummary } from "@/api/types";
import { useServices } from "@/app/context";
import { KindIcon } from "@/app/icons";
import { useEditorNavigation } from "@/app/navigation";
import { useUndoRedo } from "@/app/shortcuts";
import { useEditor, WORKSPACES } from "@/state/store";
import { PANEL_KEYS, PANEL_NAMES, PANELS } from "@/state/layout";
import { forgetPage, projectPageId } from "@/state/pageState";
import { GO_TO_SCREEN, KIND_LABELS, SCREEN_LABELS } from "@/model/labels";
import { searchClient, useSearchRows, useSearchVersion } from "@/search/client";
import type { SearchHit } from "@/search/engine";
import { parseQuery, termMatcher } from "@/search/query";
import { Tier, bestTier } from "@/search/rank";
import { CREATE_LABELS, startDomain } from "@/explorer/create";
import { useCurrentDomain } from "@/explorer/NewElementDialog";

const itemClass =
  "flex h-[var(--mq-row-h)] cursor-pointer items-center gap-2 rounded-[6px] px-2 text-[13px] text-primary data-[selected=true]:bg-accent-subtle";
const groupClass =
  "px-1 py-1 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:text-secondary";

/** The first results shown; the rest are counted ("N more: narrow the search"). */
export const RESULT_LIMIT = 200;
/** Painted with the answer; the rest of the first 200 follow on the next task, so a keystroke paints fast. */
const FIRST_PAINT = 40;

type Mode = "palette" | "quick-open";

interface LocalItem {
  value: string;
  label: string;
  /** Other words it answers to ("screen Mappings"). */
  alias?: string;
  /** Its keyboard shortcut, shown on the right. */
  keys?: string;
  run(): void;
}

interface Row {
  value: string;
  group: "Commands" | "Screens" | "Results";
  tier: number;
  local?: LocalItem;
  hit?: SearchHit;
}

/** Ranks the palette's own items with the same syntax and tiers as the worker. */
export function rankLocal(items: readonly LocalItem[], text: string): { item: LocalItem; tier: number }[] {
  const query = parseQuery(text);
  if (!query.term) return items.map((item) => ({ item, tier: Tier.Exact }));
  const match = query.explicit ? termMatcher(query) : undefined;
  const out: { item: LocalItem; tier: number }[] = [];
  for (const item of items) {
    const names = [item.label, item.alias];
    const tier = match ? (names.some((n) => n && match(n.toLowerCase())) ? Tier.Exact : Tier.None) : bestTier(query.term, names);
    if (tier !== Tier.None) out.push({ item, tier });
  }
  return out.sort((a, b) => a.tier - b.tier);
}

function useCommands(close: () => void): LocalItem[] {
  const { store } = useServices();
  const theme = useEditor(store, (s) => s.theme);
  const canApply = useEditor(store, (s) => s.generation.planId !== null && s.generation.applyJob === null);
  const { openWorkspace } = useEditorNavigation();
  const { undo, redo } = useUndoRedo();
  const domainNow = useCurrentDomain();
  const project = useProject().data;
  const pageId = project ? projectPageId(project) : null;
  return useMemo(() => {
    const run = (action: () => void) => () => {
      close();
      action();
    };
    const s = () => store.getState();
    const list: LocalItem[] = [
      { value: "cmd:theme", label: "Toggle theme", run: run(() => s().setTheme(theme === "dark" ? "light" : "dark")) },
      { value: "cmd:undo", label: "Undo", run: run(() => void undo()) },
      { value: "cmd:redo", label: "Redo", run: run(() => void redo()) },
      {
        value: "cmd:plan",
        label: "Plan generation",
        run: run(() => {
          openWorkspace("generate");
          s().requestCommand("plan");
        }),
      },
    ];
    list.push({
      value: "cmd:new-pack",
      label: "New pack…",
      run: run(() => {
        s().setSidebar("generate");
        openWorkspace("generate");
        s().setGeneration({ newPack: true });
      }),
    });
    if (canApply)
      list.push({
        value: "cmd:apply",
        label: "Apply plan",
        run: run(() => {
          openWorkspace("generate");
          s().requestCommand("apply");
        }),
      });
    list.push(
      {
        value: "cmd:new-entity",
        label: "New entity",
        run: run(() => {
          openWorkspace("entities");
          s().requestCommand("new-entity");
        }),
      },
      ...(["package", "relation", "enum", "value-object", "scalar-type", "reference-type", "diagram", "database"] as const).map((kind) => ({
        value: `cmd:new-${kind}`,
        label: CREATE_LABELS[kind],
        run: run(() => s().requestNew({ kind, domain: startDomain(kind, domainNow()) })),
      })),
      { value: "cmd:quick-open", label: "Quick open", alias: "Go to file", run: run(() => s().setQuickOpen(true)) },
      ...PANELS.map((panel) => ({
        value: `cmd:${panel}`,
        label: `Toggle ${PANEL_NAMES[panel]}`,
        alias: `hide show ${PANEL_NAMES[panel]} panel`,
        keys: PANEL_KEYS[panel].label,
        run: run(() => s().toggle(panel)),
      })),
      { value: "cmd:reset-layout", label: "Reset layout", alias: "panels default sizes", run: run(() => s().resetLayout()) },
      {
        value: "cmd:reset-page-state",
        label: "Reset layout and page state",
        alias: "forget tabs expanded rows explorer selection",
        run: run(() => {
          s().resetLayout();
          forgetPage(store, pageId);
        }),
      },
    );
    return list;
  }, [store, theme, canApply, openWorkspace, undo, redo, close, domainNow, pageId]);
}

function SearchDialog({ mode }: { mode: Mode }) {
  const { store } = useServices();
  const open = useEditor(store, (s) => (mode === "palette" ? s.paletteOpen : s.quickOpen));
  const selection = useEditor(store, (s) => s.selection);
  const recent = useEditor(store, (s) => s.recent);
  const index = useIndex();
  useSearchRows(index.data);
  const version = useSearchVersion();
  const { reveal, openWorkspace, openDatabase } = useEditorNavigation();
  const [text, setText] = useState("");
  const [answer, setAnswer] = useState<{ text: string; hits: SearchHit[]; total: number } | null>(null);
  const [selected, setSelected] = useState("");
  const [full, setFull] = useState(false);
  const enterWaiting = useRef(false);

  const setOpen = (value: boolean) => (mode === "palette" ? store.getState().setPaletteOpen(value) : store.getState().setQuickOpen(value));
  const closeRef = useRef(() => setOpen(false));
  closeRef.current = () => setOpen(false);
  const close = useMemo(() => () => closeRef.current(), []);
  const commands = useCommands(close);

  useEffect(() => {
    if (!open) {
      setText("");
      setAnswer(null);
    }
  }, [open]);

  const byId = useMemo(() => new Map((index.data ?? []).map((r) => [r.id, r] as const)), [index.data]);
  const commandsOnly = text.startsWith(">");
  const term = commandsOnly ? text.slice(1) : text;

  // The worker's ranked hits for the current text; the latest text wins.
  useEffect(() => {
    if (!open || commandsOnly || !term.trim()) return;
    let live = true;
    const current = selection.length === 1 ? byId.get(selection[0]) : undefined;
    const domain = current ? (current.kind === "package" ? current.id : (current.package ?? null)) : null;
    void searchClient()
      .rank(term, RESULT_LIMIT, { domain, recent })
      .then((a) => {
        if (!live) return;
        setFull(false);
        setAnswer({ text, hits: a.hits, total: a.total });
      });
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- selection and recent are read when the text changes
  }, [open, text, version]);

  const screens = useMemo<LocalItem[]>(
    () =>
      WORKSPACES.map((w) => ({ value: `screen:${w}`, label: SCREEN_LABELS[w], alias: `screen ${SCREEN_LABELS[w]}`, run: () => (close(), openWorkspace(w)) })),
    [openWorkspace, close],
  );

  const waiting = !commandsOnly && !!term.trim() && answer?.text !== text;
  const rows = useMemo<Row[]>(() => {
    const local: Row[] = [];
    if (mode === "palette" || commandsOnly)
      for (const { item, tier } of rankLocal(commands, term)) local.push({ value: item.value, group: "Commands", tier, local: item });
    if (!commandsOnly) for (const { item, tier } of rankLocal(screens, term)) local.push({ value: item.value, group: "Screens", tier, local: item });
    if (commandsOnly || !term.trim()) return local;
    const all = answer && answer.text === text ? answer.hits : [];
    const hits = full ? all : all.slice(0, FIRST_PAINT);
    // Merge by tier: the palette's own items first among equals.
    const merged: Row[] = [];
    let i = 0;
    for (const hit of hits) {
      while (i < local.length && local[i].tier <= hit.tier) merged.push(local[i++]);
      merged.push({ value: `hit:${hit.id}`, group: "Results", tier: hit.tier, hit });
    }
    while (i < local.length) merged.push(local[i++]);
    return merged;
  }, [mode, commandsOnly, commands, screens, term, text, answer, full]);
  useEffect(() => {
    if (full || !answer || answer.hits.length <= FIRST_PAINT) return;
    const t = setTimeout(() => setFull(true), 0);
    return () => clearTimeout(t);
  }, [answer, full]);

  const runRow = (row: Row | undefined) => {
    if (!row) return;
    if (row.local) {
      row.local.run();
      return;
    }
    const hit = row.hit!;
    close();
    if (hit.db) {
      store.getState().setSidebar("databases");
      openDatabase(hit.db);
      return;
    }
    const summary: ElementSummary | undefined = byId.get(hit.id);
    if (!summary) return;
    store.getState().noteRecent(hit.id);
    if (hit.place !== "settings") store.getState().setSidebar(hit.place);
    reveal(summary);
  };

  // The first row is selected whenever the list changes; Enter before the worker answered runs the first result.
  const first = rows[0]?.value ?? "";
  useEffect(() => {
    setSelected(first);
    if (enterWaiting.current && !waiting) {
      enterWaiting.current = false;
      runRow(rows[0]);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs when the list's answer changes
  }, [first, waiting, rows]);

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === "Enter" && waiting) {
      e.preventDefault();
      enterWaiting.current = true;
    }
  };

  const more = !commandsOnly && answer?.text === text ? answer.total - answer.hits.length : 0;
  const groups: Row["group"][] = ["Commands", "Screens", "Results"];
  const ordered = term.trim() && !commandsOnly;
  const label = mode === "palette" ? "Command palette" : "Quick open";

  return (
    <Command.Dialog
      open={open}
      onOpenChange={setOpen}
      label={label}
      shouldFilter={false}
      loop
      value={selected}
      onValueChange={setSelected}
      onKeyDown={onKeyDown}
      overlayClassName="fixed inset-0 z-40 bg-[color-mix(in_srgb,var(--mq-bg-app)_60%,transparent)]"
      contentClassName="fixed left-1/2 top-[15vh] z-50 w-[min(560px,calc(100vw-32px))] -translate-x-1/2 overflow-hidden rounded-[8px] border border-default bg-raised shadow-lg"
      data-testid={mode === "palette" ? "command-palette" : "quick-open"}
    >
      <Command.Input
        value={text}
        onValueChange={setText}
        placeholder={mode === "palette" ? "Go to an element or run a command…" : "Go to an element, table, diagram or screen…"}
        aria-label={mode === "palette" ? "Search commands and the model" : "Search the model by name"}
        className="h-7 w-full border-b border-default bg-transparent px-2 text-[12px] text-primary outline-none placeholder:text-secondary"
      />
      <Command.List className="max-h-[50vh] overflow-y-auto" data-answered={waiting ? undefined : text}>
        {!rows.length && !waiting ? <Command.Empty className="px-2 py-1 text-[13px] text-secondary">Nothing matches.</Command.Empty> : null}
        {ordered ? (
          <Command.Group className={groupClass}>
            {rows.map((row) => (
              <ResultItem key={row.value} row={row} onSelect={() => runRow(row)} />
            ))}
          </Command.Group>
        ) : (
          groups.map((g) => {
            const list = rows.filter((r) => r.group === g);
            return list.length ? (
              <Command.Group key={g} heading={g === "Screens" ? GO_TO_SCREEN : g} className={groupClass}>
                {list.map((row) => (
                  <ResultItem key={row.value} row={row} onSelect={() => runRow(row)} />
                ))}
              </Command.Group>
            ) : null;
          })
        )}
        {more > 0 ? (
          <p className="px-2 py-2 text-[12px] text-secondary" data-testid="search-more">
            {more.toLocaleString("en-US")} more: narrow the search
          </p>
        ) : null}
      </Command.List>
    </Command.Dialog>
  );
}

function ResultItem({ row, onSelect }: { row: Row; onSelect: () => void }) {
  if (row.local)
    return (
      <Command.Item value={row.value} className={itemClass} onSelect={onSelect}>
        <span className="truncate">{row.local.label}</span>
        {row.group === "Screens" ? <span className="ml-auto text-[11px] text-secondary">Screen</span> : null}
        {row.local.keys ? <span className="ml-auto font-mono text-[11px] text-secondary">{row.local.keys}</span> : null}
      </Command.Item>
    );
  const hit = row.hit!;
  const kind = KIND_LABELS[hit.kind as ElementKind] ?? hit.kind;
  return (
    <Command.Item value={row.value} className={itemClass} onSelect={onSelect} data-testid={`search-hit-${hit.name}`}>
      <KindIcon kind={hit.kind as ElementKind} className="size-4 shrink-0 text-secondary" />
      <span className="truncate">{hit.name}</span>
      {hit.display ? <span className="truncate text-[12px] text-secondary">{hit.display}</span> : null}
      <span className="ml-auto flex min-w-0 shrink-0 items-center gap-2 text-[11px] text-secondary">
        {hit.path ? <span className="max-w-48 truncate">{hit.path}</span> : null}
        <span>{kind}</span>
      </span>
    </Command.Item>
  );
}

export function CommandPalette() {
  return <SearchDialog mode="palette" />;
}

export function QuickOpen() {
  return <SearchDialog mode="quick-open" />;
}
