// The simulation panel (phase-3-design.md 6.4), docked under the statechart canvas on the Chart tab: Start, Enabled,
// Time, Pending, Configuration, Last step and Trace as dense lists (ROW_H rows, 12 px text, collapsible sections),
// Record to scenario… and Replay scenario…. The session (the start and the inputs) lives here, in the browser; every
// configuration, guard result, refusal, gate progress and verdict shown is read from the engine's answers
// (useSimulation.ts, simulate and verify), never computed here.
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { local } from "@/lib/storage";
import { useQueryClient } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, CirclePlay, Play, RotateCcw, Trash2, X } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { elementQuery, keys, upsertIndexRow, useIndex } from "@/api/queries";
import type { AttributeDoc, ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { Button, iconTitle } from "@/components/ui/button";
import { describeVerb, verbGroups } from "@/model/verbList";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from "@/components/ui/menu";
import { useVerbs } from "@/verbs/useVerbs";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { Badge, Spinner } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { indexLookup } from "@/model/index";
import { RIGHT_LABELS as RL, SIMULATION_LABELS as L } from "@/model/labels";
import { summaryFromDocument } from "@/model/model";
import type { ParticipantDoc, ScenarioDoc, ScenarioObjectDoc } from "@/model/process";
import { verifyScenario } from "../api";
import { attributeLabels, castValues, entityChain } from "./castNames";
import { objectStatesText, type StateChoiceGroup } from "@/model/effects";
import { useLifecycleStates } from "../../scenario/ParticipantsGrid";
import type { ProcessContext } from "../shared";
import { recordScenario, simulateProcess, type ScenarioVerification, type SimInput, type SimulationResult } from "./api";
import {
  attributeDefault,
  builtinType,
  coerce,
  configurationRows,
  defaultRowValue,
  enabledRows,
  failureLines,
  inputLabel,
  inputOf,
  isDuration,
  lastStepLines,
  mergeRowEdit,
  namesOf,
  nextDue,
  pendingRows,
  replayRows,
  replaySummary,
  traceRows,
  type EnabledRow,
  type Names,
  type RowValue,
} from "./model";
import { consumeSimulationRequest, useSimulationRequest } from "./requests";
import { addInput, atEnd, castOf, loadScenario, loadScenarioStart, removeFrom, resetSession, restart, selectEntry, setStartValue, shownEntry } from "./session";
import { useSimulation } from "./useSimulation";
import { setSimulationView } from "./view";
import { useSetContext } from "../SetsEditor";

const ROW = "flex h-[var(--mq-row-h)] min-w-0 items-center gap-1.5 border-b border-default px-1.5 text-12";
const FIELD = "h-5 min-w-0 rounded-[4px] border border-input bg-surface px-1 text-12 text-primary";
const SMALL = "h-5 px-1.5 text-12";

const NO_PARTICIPANTS: readonly ParticipantDoc[] = [];

type SectionId = "start" | "cast" | "enabled" | "time" | "pending" | "configuration" | "lastStep" | "trace" | "replay";

interface ReplayState {
  scenario: ScenarioDoc;
  verification: ScenarioVerification;
  simulation: SimulationResult | null;
  selected: number;
}

const isField = (el: EventTarget | null) => el instanceof HTMLElement && /^(INPUT|SELECT|TEXTAREA)$/.test(el.tagName);

/** Whether the panel is expanded, kept per browser. */
const PANEL_OPEN_KEY = "mq.simulation.open";

export function SimulationPanel({ pc }: { pc: ProcessContext }) {
  const { id, process, actors, actorNames } = pc;
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const { session, update, busy, publish } = useSimulation(id);
  const request = useSimulationRequest(id);
  const index = useIndex();
  // Collapsed until the user expands it or the explorer asks for it (Simulate, Record from simulation), so the chart
  // keeps its room; the choice is kept per browser.
  const [collapsed, setCollapsedState] = useState(() => local.get(PANEL_OPEN_KEY) !== "true");
  const setCollapsed = useCallback((next: boolean | ((c: boolean) => boolean)) => {
    setCollapsedState((c) => {
      const value = typeof next === "function" ? next(c) : next;
      local.set(PANEL_OPEN_KEY, value ? "false" : "true");
      return value;
    });
  }, []);
  const [closed, setClosed] = useState<ReadonlySet<SectionId>>(new Set());
  const [recording, setRecording] = useState<{ name: string } | null>(null);
  const [recordOpen, setRecordOpen] = useState(false);
  const [replayOpen, setReplayOpen] = useState(false);
  const [replay, setReplay] = useState<ReplayState | null>(null);
  const [replayBusy, setReplayBusy] = useState(false);
  const [edits, setEdits] = useState<Record<string, Partial<RowValue>>>({});
  const panelRef = useRef<HTMLElement>(null);
  const focusEnabled = useRef(false);

  const participants = (replay ? replay.scenario.participants : session.start.participants) ?? NO_PARTICIPANTS;
  // What a refusal points at: the lifecycles and named rights of the project, by name (the process's own parts name themselves).
  const elementNames = useMemo(
    () => new Map((index.data ?? []).filter((r) => r.kind === "process" || r.kind === "right").map((r) => [r.id, r.displayName || r.name] as [string, string])),
    [index.data],
  );
  const names = useMemo(() => namesOf(process, actorNames, participants, elementNames), [process, actorNames, participants, elementNames]);
  const entityNames = useMemo(() => new Map((index.data ?? []).filter((r) => r.kind === "entity").map((r) => [r.id, r.name])), [index.data]);
  const allActors = useMemo(() => actors.map((a) => ({ id: a.id, name: a.name })), [actors]);
  const scenarios = useMemo(
    () =>
      indexLookup(index.data)
        .ofKind("scenario")
        .filter((s) => s.process === id)
        .sort((a, b) => a.name.localeCompare(b.name)),
    [index.data, id],
  );
  const result = session.result;
  // While a replay is shown, Configuration and Last step describe its selected step (the engine's simulation of it).
  const replayEntry = replay?.simulation?.trace.find((t) => t.index === replay.selected) ?? null;
  const entry = replay ? replayEntry : shownEntry(session);
  const live = atEnd(session);
  // The run's objects: the replayed scenario's while a replay is shown, else the session's start.
  const cast = replay
    ? { objects: replay.scenario.objects ?? [], subject: replay.scenario.subject }
    : { objects: session.start.objects ?? [], subject: session.start.subject };
  // Attribute names for the cast's values, by entity (the entity's own attributes and its bases').
  const [castLabels, setCastLabels] = useState<ReadonlyMap<string, ReadonlyMap<string, string>>>(new Map());
  const castEntities = [...new Set(cast.objects.map((o) => o.entity))].sort().join(",");
  const stateGroups = useLifecycleStates(cast.objects.some((o) => o.states?.length) ? castEntities.split(",").filter(Boolean) : [], id);
  // The process's own subjects name the attributes its actions set, with or without objects in the cast.
  const setContext = useSetContext(process);
  const subjectAttribute = (attribute: string) =>
    [...castLabels.values()].find((m) => m.has(attribute))?.get(attribute) ?? setContext.names.name(attribute) ?? attribute;
  useEffect(() => {
    let active = true;
    const wanted = castEntities ? castEntities.split(",") : [];
    void Promise.all(wanted.map(async (e) => [e, attributeLabels(await entityChain(qc, e))] as const)).then((pairs) => {
      if (active) setCastLabels(new Map(pairs));
    });
    return () => {
      active = false;
    };
  }, [castEntities, qc]);
  const rows = useMemo(
    () => (result && live && !result.final ? enabledRows(result, process, names, allActors) : []),
    [result, live, process, names, allActors],
  );
  const valueOf = useCallback(
    (row: EnabledRow): RowValue => {
      const base = defaultRowValue(row, result!);
      const edit = edits[row.key.replace(/:\d+$/, "")] ?? {};
      return { ...base, ...edit, payload: { ...base.payload, ...edit.payload }, assume: { ...base.assume, ...edit.assume } };
    },
    [edits, result],
  );

  // The explorer asked for this panel (Simulate, or Record from simulation with a name): show it, then drop the request.
  useEffect(() => {
    if (!request) return;
    setCollapsed(false);
    if (request.record) setRecording({ name: request.record.name });
    consumeSimulationRequest(request);
    panelRef.current?.focus();
  }, [request, setCollapsed]);

  // After a raise from the keyboard, the focus goes to the first enabled row of the new answer.
  useEffect(() => {
    if (!focusEnabled.current || !result) return;
    focusEnabled.current = false;
    const first = panelRef.current?.querySelector<HTMLElement>('[data-testid="enabled-row"]');
    (first ?? panelRef.current)?.focus();
  }, [result]);

  const raise = (row: EnabledRow, fromKeyboard = false) => {
    if (!result) return;
    const input = inputOf(row, valueOf(row));
    const key = row.key.replace(/:\d+$/, "");
    // The actor and payload chosen stay for the next raise; the signer, meaning, reason and assumptions go back to
    // their defaults (a fresh signer name, so raising a gated event again adds another signature).
    setEdits((e) => {
      const kept: Partial<RowValue> = {};
      if (e[key]?.actor !== undefined) kept.actor = e[key].actor;
      if (e[key]?.participant !== undefined) kept.participant = e[key].participant;
      if (e[key]?.payload !== undefined) kept.payload = e[key].payload;
      return { ...e, [key]: kept };
    });
    focusEnabled.current = fromKeyboard;
    if (fromKeyboard) panelRef.current?.focus();
    update((s) => addInput(s, input));
  };
  const edit = (row: EnabledRow, patch: Partial<RowValue>) => {
    const key = row.key.replace(/:\d+$/, "");
    setEdits((e) => ({ ...e, [key]: mergeRowEdit(e[key], patch) }));
  };
  const toggle = (section: SectionId) =>
    setClosed((c) => {
      const next = new Set(c);
      if (next.has(section)) next.delete(section);
      else next.add(section);
      return next;
    });

  const onPanelKey = (e: KeyboardEvent<HTMLElement>) => {
    if (e.ctrlKey || e.metaKey || e.altKey || isField(e.target)) return;
    if (!/^[1-9]$/.test(e.key)) return;
    const row = rows[Number(e.key) - 1];
    if (!row) return;
    e.preventDefault();
    raise(row, true);
  };

  const loadFrom = async (scenario: string) => {
    try {
      const doc = await qc.fetchQuery(elementQuery(scenario));
      const json = doc.json as unknown as ScenarioDoc;
      setReplay(null);
      update((s) => loadScenario(s, json));
      store.getState().notify(L.loadedScenario(json.name));
    } catch (e) {
      store.getState().notify(L.failed((e as Error).message), "error");
    }
  };

  const record = async (name: string, outcome: "final" | "active"): Promise<string | null> => {
    try {
      // Record replays the saved process: a draft that could not be saved would make it record another process.
      if (!(await drafts.flushSaved(id))) return L.unsavedDraft;
      const start = { context: session.start.context, ...(session.start.at ? { at: session.start.at } : {}) };
      const saved = await recordScenario(id, { name, start, ...castOf(session.start), steps: session.inputs, outcome });
      // The index and validation follow the new scenario (as a save does); one undo step deletes it.
      const doc = await endpoints.getElement(saved.id);
      qc.setQueryData(keys.element(saved.id), doc);
      upsertIndexRow(qc, summaryFromDocument(doc));
      void qc.invalidateQueries({ queryKey: keys.validation });
      store.getState().pushUndo({ label: L.recordUndo(name), ids: [saved.id], before: [null], after: [doc.json as ModelJson], afterHashes: [doc.hash] });
      store.getState().notify(L.recorded(name));
      setRecording(null);
      return null;
    } catch (e) {
      return (e as Error).message;
    }
  };

  const runReplay = async (scenario: string): Promise<string | null> => {
    setReplayBusy(true);
    try {
      // Verify and simulate read the saved process: refuse while a draft that could not be saved remains.
      if (!(await drafts.flushSaved(id))) return L.unsavedDraft;
      const doc = (await qc.fetchQuery(elementQuery(scenario))).json as unknown as ScenarioDoc;
      // The verdicts are the engine's (verify); the states after each step are the engine's simulation of the scenario.
      const verified = await verifyScenario(id, scenario);
      const verification = verified.results.find((r) => r.scenario === scenario);
      if (!verification) return L.noScenarios;
      const simulated = await simulateProcess(id, { scenario });
      const simulation = simulated.kind === "result" ? simulated.result : null;
      const failed = verification.failure?.step ?? -1;
      const selected = verification.passed || failed < 0 ? doc.steps.length - 1 : failed;
      setReplay({ scenario: doc, verification, simulation, selected });
      // The session takes the scenario's start values, as picking it under Start does; the steps are the replay's.
      update((x) => loadScenarioStart(x, doc));
      return null;
    } catch (e) {
      return (e as Error).message;
    } finally {
      setReplayBusy(false);
    }
  };

  // The canvas follows the replay's selected step while a replay is shown, and the session again once it closes.
  useEffect(() => {
    if (!replay) return;
    const t = replay.simulation?.trace.find((x) => x.index === replay.selected);
    if (t)
      setSimulationView({ process: id, configuration: t.configuration, taken: t.accepted ? t.microsteps.flatMap((m) => m.transitions) : [], step: t.index });
  }, [replay, id]);
  const closeReplay = () => {
    setReplay(null);
    publish();
  };

  const section = (sid: SectionId, title: string, body: ReactNode, extra?: ReactNode) => (
    <Section id={sid} title={title} open={!closed.has(sid)} onToggle={() => toggle(sid)} extra={extra}>
      {body}
    </Section>
  );

  return (
    <section
      ref={panelRef}
      tabIndex={-1}
      aria-label={L.panelOf(process.name)}
      data-testid="simulation-panel"
      data-busy={busy ? "true" : "false"}
      className={cn("flex shrink-0 flex-col border-t border-default bg-surface text-12 outline-none", collapsed ? "h-auto" : "h-[45%] min-h-40")}
      onKeyDown={onPanelKey}
    >
      <div className="flex h-[var(--mq-row-h)] shrink-0 items-center gap-2 border-b border-default px-1.5" data-testid="simulation-toolbar">
        <Button
          variant="ghost"
          size="icon-row"
          label={collapsed ? L.expand : L.collapse}
          aria-expanded={!collapsed}
          onClick={() => setCollapsed((c) => !c)}
          data-testid="simulation-collapse"
        >
          {collapsed ? <ChevronRight /> : <ChevronDown />}
        </Button>
        <h3 className="text-11 font-semibold uppercase tracking-wide text-secondary">{L.panel}</h3>
        <span className="text-secondary" data-testid="simulation-status">
          {busy ? L.running : L.inputs(session.inputs.length)}
          {result?.final ? ` · ${L.final}` : ""}
        </span>
        {recording ? (
          <Badge tone="accent" data-testid="simulation-recording">
            {L.recording(recording.name)}
            <button
              type="button"
              className="ml-1 text-secondary hover:text-primary"
              aria-label={L.stopRecording}
              title={L.stopRecording}
              onClick={() => setRecording(null)}
            >
              <X className="size-3" />
            </button>
          </Badge>
        ) : null}
        <span className="ml-auto flex items-center gap-1">
          <Button variant="ghost" size="icon-row" label={L.reset} onClick={() => update(resetSession)} data-testid="simulation-reset">
            <RotateCcw />
          </Button>
          <Button
            variant={recording ? "primary" : "secondary"}
            size="sm"
            className={SMALL}
            disabled={!session.inputs.length}
            title={session.inputs.length ? undefined : L.recordNothing}
            onClick={() => setRecordOpen(true)}
            data-testid="simulation-record"
          >
            {L.record}
          </Button>
          <Button variant="secondary" size="sm" className={SMALL} onClick={() => setReplayOpen(true)} data-testid="simulation-replay">
            {L.replayScenario}
          </Button>
        </span>
      </div>
      {!collapsed ? (
        <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,3fr)_minmax(0,2fr)_minmax(0,2fr)] divide-x divide-default">
          <div className="min-h-0 overflow-auto">
            {section(
              "start",
              L.start,
              <StartSection
                pc={pc}
                session={session}
                onStart={(attr, v) => update((s) => setStartValue(s, attr, v))}
                onAt={(at) => update((s) => restart(s, { ...s.start, at }))}
              />,
              <Select
                aria-label={L.fromScenario}
                className={cn(FIELD, "w-32")}
                value=""
                onChange={(e) => e.target.value && void loadFrom(e.target.value)}
                data-testid="simulation-from-scenario"
              >
                <option value="">{L.fromScenarioPlaceholder}</option>
                {scenarios.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.displayName || s.name}
                  </option>
                ))}
              </Select>,
            )}
            {section(
              "enabled",
              L.enabled,
              <EnabledSection
                participants={participants}
                rows={rows}
                result={result}
                live={live}
                replay={replay ? { final: !!replayEntry?.final } : null}
                entryLabel={traceLabel(session, session.selected ?? -1, names)}
                valueOf={valueOf}
                onEdit={edit}
                onRaise={raise}
              />,
              process.use === "lifecycle" ? (
                <DoMenu
                  participants={participants}
                  actors={allActors}
                  disabled={!result || !live || !!result.final || !!replay}
                  onDo={(verb, who) => update((s) => addInput(s, { input: "verb", verb, ...who }))}
                />
              ) : undefined,
            )}
            {section(
              "time",
              L.time,
              <TimeSection result={result} live={live} names={names} onAdvance={(after) => update((s) => addInput(s, { input: "time", after }))} />,
            )}
            {section(
              "pending",
              L.pending,
              <PendingSection
                result={result}
                live={live}
                pc={pc}
                names={names}
                onInput={(kind, invoke, actor) => update((s) => addInput(s, { input: kind, invoke, ...(actor ? { actor } : {}) }))}
              />,
            )}
          </div>
          <div className="min-h-0 overflow-auto">
            <Messages session={session} />
            {cast.objects.length
              ? section(
                  "cast",
                  L.cast,
                  <CastSection objects={cast.objects} subject={cast.subject} entityNames={entityNames} labels={castLabels} stateGroups={stateGroups} />,
                )
              : null}
            {section(
              "configuration",
              L.configuration,
              <ConfigurationSection configuration={entry?.configuration ?? []} final={!!entry?.final} names={names} />,
            )}
            {section("lastStep", L.lastStep, <LastStepSection entry={entry} names={names} subjectAttribute={subjectAttribute} />)}
          </div>
          <div className="min-h-0 overflow-auto">
            {replay
              ? section(
                  "replay",
                  L.replayOf(scenarios.find((s) => s.name === replay.scenario.name)?.displayName || replay.scenario.name),
                  <ReplaySection replay={replay} names={names} onSelect={(i) => setReplay({ ...replay, selected: i })} />,
                  <Button variant="ghost" size="icon-row" label={L.replayClose} onClick={closeReplay} data-testid="replay-close">
                    <X />
                  </Button>,
                )
              : section(
                  "trace",
                  L.trace,
                  <TraceSection
                    session={session}
                    names={names}
                    onSelect={(i) => update((s) => selectEntry(s, i))}
                    onDelete={(i) => update((s) => (i < 0 ? resetSession(s) : removeFrom(s, i)))}
                  />,
                )}
          </div>
        </div>
      ) : null}
      {recordOpen ? (
        <RecordDialog
          initialName={recording?.name ?? ""}
          initialOutcome={result?.final ? "final" : "active"}
          onRecord={record}
          onClose={() => setRecordOpen(false)}
        />
      ) : null}
      {replayOpen ? (
        <ReplayDialog
          scenarios={scenarios}
          busy={replayBusy}
          onReplay={async (s) => {
            const error = await runReplay(s);
            if (!error) setReplayOpen(false);
            return error;
          }}
          onClose={() => setReplayOpen(false)}
        />
      ) : null}
    </section>
  );
}

/** The trace row's label of an entry: Start, or the input's words. */
function traceLabel(session: { inputs: SimInput[] }, index: number, names: Names): string {
  const input = session.inputs[index];
  return index < 0 || !input ? L.traceStart : inputLabel(input, names);
}

// ------------------------------------------------------------------ sections

function Section({
  id,
  title,
  open,
  onToggle,
  extra,
  children,
}: {
  id: SectionId;
  title: string;
  open: boolean;
  onToggle: () => void;
  extra?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section aria-label={title} data-testid={`simulation-${id}`} data-open={open ? "true" : "false"}>
      <div className="sticky top-0 z-[1] flex h-[var(--mq-row-h)] items-center gap-1 border-b border-default bg-app px-1">
        <Button variant="ghost" size="icon-row" label={open ? L.collapseSection(title) : L.expandSection(title)} aria-expanded={open} onClick={onToggle}>
          {open ? <ChevronDown /> : <ChevronRight />}
        </Button>
        <h4 className="min-w-0 flex-1 truncate text-11 font-semibold uppercase tracking-wide text-secondary">{title}</h4>
        {extra}
      </div>
      {open ? children : null}
    </section>
  );
}

/** The Do… menu (rights-design.md section 10): a verb done to the object, as the chosen participant or actor. */
function DoMenu({
  participants,
  actors,
  disabled,
  onDo,
}: {
  participants: readonly ParticipantDoc[];
  actors: readonly { id: string; name: string }[];
  disabled: boolean;
  onDo: (verb: string, who: { participant?: string; actor?: string }) => void;
}) {
  const verbs = useVerbs();
  const [who, setWho] = useState("");
  const groups = verbGroups("right", verbs.own, verbs.hidden)
    .filter((g) => g.id !== "move" && g.id !== "signature" && g.id !== "task")
    .map((g) => ({ ...g, options: g.options.filter((o) => o.key !== "freeze" && o.key !== "thaw") }))
    .filter((g) => g.options.length);
  const pick = (): { participant?: string; actor?: string } =>
    who.startsWith("p:") ? { participant: who.slice(2) } : who.startsWith("a:") ? { actor: who.slice(2) } : {};
  return (
    <span className="flex items-center gap-1">
      <Select aria-label={RL.doAs} className={cn(FIELD, "w-28")} value={who} onChange={(e) => setWho(e.target.value)} data-testid="do-as">
        <option value="">{RL.doAsNobody}</option>
        {participants.map((p) => (
          <option key={p.id} value={`p:${p.id}`}>
            {p.name}
          </option>
        ))}
        {actors.map((a) => (
          <option key={a.id} value={`a:${a.id}`}>
            {a.name}
          </option>
        ))}
      </Select>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="secondary" size="sm" className={SMALL} disabled={disabled} title={RL.doMenuTip} data-testid="do-menu">
            {RL.doMenu}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" data-testid="do-menu-content">
          {groups.map((g) => (
            <div key={g.id}>
              <DropdownMenuLabel>{g.label}</DropdownMenuLabel>
              {g.options.map((o) => (
                <DropdownMenuItem key={o.key} onSelect={() => onDo(o.key, pick())} data-testid={`do-${o.key}`}>
                  {o.label}
                </DropdownMenuItem>
              ))}
            </div>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>
    </span>
  );
}

function Note({
  children,
  testid,
  tone = "secondary",
  wrap = false,
}: {
  children: ReactNode;
  testid?: string;
  tone?: "secondary" | "danger" | "warning";
  wrap?: boolean;
}) {
  return (
    <p
      className={cn(
        ROW,
        wrap ? "h-auto min-h-[var(--mq-row-h)] items-start whitespace-normal break-words py-0.5" : "truncate",
        tone === "secondary" && "text-secondary",
        tone === "danger" && "text-danger",
        tone === "warning" && "text-warning",
      )}
      data-testid={testid}
      title={typeof children === "string" ? children : undefined}
    >
      <span className={wrap ? "min-w-0" : "truncate"}>{children}</span>
    </p>
  );
}

function Messages({ session }: { session: { invalid: { rule: string; message: string }[] | null; error: string | null } }) {
  if (session.error)
    return (
      <Note testid="simulation-error-row" tone="danger">
        {L.failed(session.error)}
      </Note>
    );
  if (!session.invalid) return null;
  return (
    <div data-testid="simulation-invalid">
      <Note tone="danger">{L.invalidDraft}</Note>
      {session.invalid.map((d, i) => (
        <Note key={i} testid="simulation-invalid-row" tone="danger">
          {`${d.rule} ${d.message}`}
        </Note>
      ))}
    </div>
  );
}

/** One compact value: "total 120"; text stays plain, structures are written out. */
function compactValue(v: unknown): string {
  return typeof v === "string" ? v : typeof v === "object" && v !== null ? JSON.stringify(v) : String(v);
}

/** The run's objects, read-only (edited in the scenario's JSON view): name, entity and values; the subject is marked. */
function CastSection({
  objects,
  subject,
  entityNames,
  labels,
  stateGroups,
}: {
  objects: readonly ScenarioObjectDoc[];
  subject?: string;
  entityNames: ReadonlyMap<string, string>;
  labels: ReadonlyMap<string, ReadonlyMap<string, string>>;
  stateGroups: ReadonlyMap<string, StateChoiceGroup[]>;
}) {
  return (
    <ul title={L.objectsHint} data-testid="start-objects">
      {objects.map((o) => {
        const states = objectStatesText(o.states, stateGroups.get(o.entity) ?? []);
        const values = [castValues(o.values, labels.get(o.entity) ?? new Map(), compactValue), states ? `${L.objectStates} ${states}` : ""]
          .filter(Boolean)
          .join("; ");
        return (
          <li
            key={o.id}
            className={cn(ROW, "h-auto min-h-[var(--mq-row-h)] flex-wrap py-0.5")}
            data-testid="start-object"
            data-subject={o.id === subject}
            title={values}
          >
            <span className="max-w-[30%] shrink-0 truncate font-medium">{o.name}</span>
            <span className="max-w-[30%] shrink-0 truncate text-secondary">{entityNames.get(o.entity) ?? o.entity}</span>
            {o.id === subject ? <span className="shrink-0 text-secondary">({L.objectSubject})</span> : null}
            <span className="min-w-0 flex-1 break-words font-mono text-11 text-secondary">{values}</span>
          </li>
        );
      })}
    </ul>
  );
}

function StartSection({
  pc,
  session,
  onStart,
  onAt,
}: {
  pc: ProcessContext;
  session: { start: { context: Record<string, unknown>; at?: string; objects?: ScenarioObjectDoc[]; subject?: string } };
  onStart: (attribute: string, value: unknown) => void;
  onAt: (at: string | undefined) => void;
}) {
  const context = pc.process.context ?? [];
  return (
    <div>
      {context.length ? null : <Note>{L.noContext}</Note>}
      {context.map((a) => (
        <div key={a.id} className={ROW} data-testid="start-row" data-attribute={a.name}>
          <label htmlFor={`sim-start-${a.id}`} className="w-28 shrink-0 truncate text-secondary" title={a.name}>
            {a.name}
          </label>
          <ValueField
            id={`sim-start-${a.id}`}
            attribute={a}
            value={a.id in session.start.context ? session.start.context[a.id] : attributeDefault(a)}
            onCommit={(v) => onStart(a.id, v)}
          />
        </div>
      ))}
      <div className={ROW} data-testid="start-row" data-attribute="at">
        <label htmlFor="sim-start-at" className="w-28 shrink-0 truncate text-secondary">
          {L.clockStart}
        </label>
        <CommitInput id="sim-start-at" value={session.start.at ?? "2000-01-01T00:00:00Z"} onCommit={(v) => onAt(v.trim() || undefined)} />
      </div>
    </div>
  );
}

/** A text field that reports its value on Enter or blur (a start edit restarts the session, so not on each key). */
function CommitInput({
  id,
  value,
  onCommit,
  label,
  className,
}: {
  id?: string;
  value: string;
  onCommit: (v: string) => void;
  label?: string;
  className?: string;
}) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  return (
    <input
      id={id}
      aria-label={label}
      className={cn(FIELD, "flex-1", className)}
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={() => text !== value && onCommit(text)}
      onKeyDown={(e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          e.stopPropagation();
          if (text !== value) onCommit(text);
        }
      }}
    />
  );
}

function ValueField({
  id,
  attribute,
  value,
  onCommit,
  label,
}: {
  id?: string;
  attribute: AttributeDoc;
  value: unknown;
  onCommit: (v: unknown) => void;
  label?: string;
}) {
  if (builtinType(attribute) === "bool")
    return <input id={id} aria-label={label} type="checkbox" className="size-3.5" checked={value === true} onChange={(e) => onCommit(e.target.checked)} />;
  return (
    <CommitInput id={id} label={label} value={value === null || value === undefined ? "" : String(value)} onCommit={(v) => onCommit(coerce(attribute, v))} />
  );
}

function EnabledSection({
  participants,
  rows,
  result,
  live,
  replay,
  entryLabel,
  valueOf,
  onEdit,
  onRaise,
}: {
  participants: readonly ParticipantDoc[];
  rows: EnabledRow[];
  result: SimulationResult | null;
  live: boolean;
  /** A replay is shown: Enabled describes it, not the live session. */
  replay: { final: boolean } | null;
  entryLabel: string;
  valueOf: (row: EnabledRow) => RowValue;
  onEdit: (row: EnabledRow, patch: Partial<RowValue>) => void;
  onRaise: (row: EnabledRow, fromKeyboard?: boolean) => void;
}) {
  if (replay?.final) return <Note testid="enabled-final">{L.finalReached}</Note>;
  if (replay) return <Note testid="enabled-replay">{L.replayShown}</Note>;
  if (!result) return <Spinner label={L.running} />;
  if (!live) return <Note testid="enabled-past">{L.pastSelected(entryLabel)}</Note>;
  if (result.final) return <Note testid="enabled-final">{L.finalReached}</Note>;
  if (!rows.length) return <Note>{L.noEnabled}</Note>;
  return (
    <div>
      <ul aria-label={L.enabled}>
        {rows.map((row, i) => (
          <EnabledRowView
            key={row.key}
            row={row}
            participants={participants}
            n={i < 9 ? i + 1 : null}
            value={valueOf(row)}
            onEdit={(p) => onEdit(row, p)}
            onRaise={(k) => onRaise(row, k)}
          />
        ))}
      </ul>
      <p className="truncate px-1.5 py-0.5 text-11 text-secondary" title={L.enabledHint}>
        {L.enabledHint}
      </p>
    </div>
  );
}

function EnabledRowView({
  row,
  participants,
  n,
  value,
  onEdit,
  onRaise,
}: {
  row: EnabledRow;
  participants: readonly ParticipantDoc[];
  n: number | null;
  value: RowValue;
  onEdit: (patch: Partial<RowValue>) => void;
  onRaise: (fromKeyboard: boolean) => void;
}) {
  const onKey = (e: KeyboardEvent<HTMLElement>) => {
    if (e.key !== "Enter" || e.target instanceof HTMLButtonElement) return;
    e.preventDefault();
    e.stopPropagation();
    onRaise(true);
  };
  const { own } = useVerbs();
  // An event takes an actor; an invoke result only when its task names the actors who may complete it.
  const needsActor = row.trigger === "event" || ((row.trigger === "invoke-done" || row.trigger === "invoke-error") && !row.anyActor);
  return (
    <li data-trigger={row.trigger} data-label={row.label}>
      <div
        className={cn(ROW, "focus-visible:bg-accent-subtle focus-visible:outline-none")}
        tabIndex={0}
        role="group"
        aria-label={L.raiseRow(row.label, n)}
        onKeyDown={onKey}
        data-testid="enabled-row"
        data-trigger={row.trigger}
        data-label={row.label}
      >
        <span className="w-3 shrink-0 text-right text-11 text-secondary">{n ?? ""}</span>
        <span className="min-w-0 flex-1 truncate font-medium" title={row.label}>
          {row.label}
        </span>
        {row.gate ? (
          <Badge tone="accent" data-testid="enabled-gate">
            {L.gateProgress(row.gate.have, row.gate.need)}
          </Badge>
        ) : null}
        {row.trigger === "time" ? (
          <input
            aria-label={L.advanceBy}
            className={cn(FIELD, "w-20")}
            value={value.after}
            onChange={(e) => onEdit({ after: e.target.value })}
            onKeyDown={onKey}
          />
        ) : null}
        {needsActor && participants.length ? (
          <select
            aria-label={L.participant}
            className={cn(FIELD, "w-24 shrink-0")}
            value={value.participant}
            onChange={(e) =>
              onEdit({
                participant: e.target.value,
                // A participant's actor and signer are worked out by the engine; with none, the row's defaults return.
                actor: e.target.value ? "" : row.anyActor ? "" : (row.actors[0]?.id ?? ""),
                signer: e.target.value || !row.gate ? "" : `signer${row.gate.have + 1}`,
              })
            }
            onKeyDown={onKey}
            data-testid="enabled-participant"
          >
            <option value="">{L.noParticipant}</option>
            {participants.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        ) : null}
        {needsActor ? (
          <select
            aria-label={L.actor}
            className={cn(FIELD, "w-24 shrink-0")}
            value={value.actor}
            onChange={(e) => onEdit({ actor: e.target.value })}
            onKeyDown={onKey}
            data-testid="enabled-actor"
          >
            {value.participant ? <option value="">{L.fromParticipant}</option> : row.anyActor ? <option value="">{L.anyActor}</option> : null}
            {row.actors.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
        ) : null}
        <Button
          variant="ghost"
          size="icon-row"
          tabIndex={-1}
          label={L.raiseRow(row.label, n)}
          shortcut={n ? String(n) : undefined}
          title={
            row.verb ? `${iconTitle(L.raiseRow(row.label, n), n ? String(n) : undefined)}. Verb: ${describeVerb(row.verb, own)?.label ?? row.verb}` : undefined
          }
          onClick={() => onRaise(false)}
          data-testid="enabled-raise"
        >
          <Play />
        </Button>
      </div>
      {row.payload.map((a) => (
        <div key={a.id} className={cn(ROW, "pl-6")} data-testid="enabled-field-row" onKeyDown={onKey}>
          <label className="w-24 shrink-0 truncate text-secondary" htmlFor={`sim-${row.key}-${a.id}`}>
            {a.name}
          </label>
          {builtinType(a) === "bool" ? (
            <input
              id={`sim-${row.key}-${a.id}`}
              type="checkbox"
              className="size-3.5"
              checked={value.payload[a.id] === true}
              onChange={(e) => onEdit({ payload: { [a.id]: e.target.checked } })}
            />
          ) : (
            <input
              id={`sim-${row.key}-${a.id}`}
              className={cn(FIELD, "flex-1")}
              value={value.payload[a.id] === undefined || value.payload[a.id] === null ? "" : String(value.payload[a.id])}
              onChange={(e) => onEdit({ payload: { [a.id]: e.target.value } })}
            />
          )}
        </div>
      ))}
      {row.gate ? (
        <>
          <div className={cn(ROW, "pl-6")} data-testid="enabled-field-row" onKeyDown={onKey}>
            <label className="w-24 shrink-0 truncate text-secondary" htmlFor={`sim-${row.key}-signer`}>
              {L.signer}
            </label>
            <input
              id={`sim-${row.key}-signer`}
              className={cn(FIELD, "flex-1")}
              value={value.signer}
              onChange={(e) => onEdit({ signer: e.target.value })}
              data-testid="enabled-signer"
            />
          </div>
          <div className={cn(ROW, "pl-6")} data-testid="enabled-field-row" onKeyDown={onKey}>
            <label className="w-24 shrink-0 truncate text-secondary" htmlFor={`sim-${row.key}-meaning`}>
              {L.meaning}
            </label>
            <select id={`sim-${row.key}-meaning`} className={cn(FIELD, "flex-1")} value={value.meaning} onChange={(e) => onEdit({ meaning: e.target.value })}>
              {row.gate.meanings.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.name}
                </option>
              ))}
            </select>
          </div>
          <div className={cn(ROW, "pl-6")} data-testid="enabled-field-row" onKeyDown={onKey}>
            <label className="w-24 shrink-0 truncate text-secondary" htmlFor={`sim-${row.key}-reason`}>
              {row.gate.reasonRequired ? L.reasonRequired : L.reason}
            </label>
            <input
              id={`sim-${row.key}-reason`}
              className={cn(FIELD, "flex-1")}
              value={value.reason}
              onChange={(e) => onEdit({ reason: e.target.value })}
              data-testid="enabled-reason"
            />
          </div>
        </>
      ) : null}
      {row.assume.map((g) => {
        const on = value.assume[g.id] !== false;
        return (
          <div key={g.id} className={cn(ROW, "pl-6")} data-testid="enabled-field-row" onKeyDown={onKey}>
            <span className="w-24 shrink-0 truncate text-secondary">{g.name}</span>
            <Button
              variant={on ? "primary" : "secondary"}
              size="sm"
              className={SMALL}
              aria-pressed={on}
              label={L.assume(g.name)}
              onClick={() => onEdit({ assume: { [g.id]: !on } })}
              data-testid="assume-toggle"
            >
              {on ? L.assumeTrue : L.assumeFalse}
            </Button>
          </div>
        );
      })}
    </li>
  );
}

function TimeSection({ result, live, names, onAdvance }: { result: SimulationResult | null; live: boolean; names: Names; onAdvance: (after: string) => void }) {
  const next = result ? nextDue(result) : null;
  const [after, setAfter] = useState<string | null>(null);
  const shown = after ?? next?.after ?? "PT1H";
  const valid = isDuration(shown);
  if (!result) return null;
  const advance = () => {
    if (!valid || !live || result.final) return;
    onAdvance(shown.trim());
    setAfter(null);
  };
  return (
    <div>
      <Note testid="time-row">{next ? L.nextTimer(names.transition(next.transition), next.dueAt) : L.noTimers}</Note>
      <div className={ROW} data-testid="time-row">
        <span className="min-w-0 flex-1 truncate text-secondary" title={L.clock(result.clock)} data-testid="simulation-clock">
          {L.clock(result.clock)}
        </span>
        <label htmlFor="sim-advance" className="shrink-0 text-secondary">
          {L.advanceBy}
        </label>
        <input
          id="sim-advance"
          className={cn(FIELD, "w-20")}
          aria-invalid={!valid}
          title={L.durationHint}
          value={shown}
          onChange={(e) => setAfter(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              advance();
            }
          }}
          data-testid="time-after"
        />
        <Button variant="secondary" size="sm" className={SMALL} disabled={!valid || !live || result.final} onClick={advance} data-testid="time-advance">
          {L.advance}
        </Button>
      </div>
    </div>
  );
}

function PendingSection({
  result,
  live,
  pc,
  names,
  onInput,
}: {
  result: SimulationResult | null;
  live: boolean;
  pc: ProcessContext;
  names: Names;
  onInput: (kind: "invoke-done" | "invoke-error", invoke: string, actor: string | null) => void;
}) {
  if (!result) return null;
  const rows = pendingRows(result, pc.process, names);
  if (!rows.length) return <Note>{L.noPending}</Note>;
  return (
    <ul aria-label={L.pending}>
      {rows.map((p) => (
        <li key={`${p.invoke}:${p.state}`} className={ROW} data-testid="pending-row" data-invoke={names.invoke(p.invoke)}>
          <span className="min-w-0 flex-1 truncate">{p.label}</span>
          <Button
            variant="secondary"
            size="sm"
            className={SMALL}
            disabled={!live}
            title={L.doneOf(names.invoke(p.invoke))}
            onClick={() => onInput("invoke-done", p.invoke, p.actor)}
          >
            {L.done}
          </Button>
          <Button
            variant="secondary"
            size="sm"
            className={SMALL}
            disabled={!live}
            title={L.errorOf(names.invoke(p.invoke))}
            onClick={() => onInput("invoke-error", p.invoke, p.actor)}
          >
            {L.error}
          </Button>
        </li>
      ))}
    </ul>
  );
}

function ConfigurationSection({ configuration, final, names }: { configuration: readonly string[]; final: boolean; names: Names }) {
  const rows = configurationRows(configuration, names);
  if (!rows.length) return <Note>{L.noConfiguration}</Note>;
  return (
    <ul aria-label={L.configuration} data-final={final ? "true" : "false"}>
      {rows.map((r) => (
        <li key={r.id} className={ROW} data-testid="configuration-row" data-state={r.id}>
          <span className="size-2 shrink-0 rounded-full bg-accent" aria-hidden />
          <span className="truncate" title={r.path}>
            {r.path}
          </span>
          {final ? <Badge tone="success">{L.final}</Badge> : null}
        </li>
      ))}
    </ul>
  );
}

function LastStepSection({ entry, names, subjectAttribute }: { entry: ReturnType<typeof shownEntry>; names: Names; subjectAttribute: (id: string) => string }) {
  if (!entry || (entry.index < 0 && !entry.microsteps.some((m) => m.actions.length) && !entry.diagnostics.length)) return <Note>{L.noLastStep}</Note>;
  const lines = lastStepLines(entry, names, subjectAttribute);
  return (
    <ul aria-label={L.lastStep}>
      {entry.index < 0 ? (
        <li className={cn(ROW, "text-secondary")} data-testid="last-step-row" data-kind="initial">
          {L.initialEntry}
        </li>
      ) : null}
      {lines.map((line, i) => (
        <li
          key={i}
          className={cn(
            ROW,
            (line.kind === "sets" || line.kind === "values") && "h-auto min-h-[var(--mq-row-h)] items-start py-0.5",
            line.tone === "danger" && "text-danger",
            line.tone === "warning" && "text-warning",
            line.tone === "success" && "text-success",
          )}
          data-testid="last-step-row"
          data-kind={line.kind}
          title={line.text}
        >
          {line.kind === "sets" || line.kind === "values" ? (
            <span className="min-w-0 whitespace-normal break-words" data-testid="last-step-wrapped">
              {line.text}
            </span>
          ) : (
            <span className="truncate">{line.text}</span>
          )}
        </li>
      ))}
    </ul>
  );
}

function TraceSection({
  session,
  names,
  onSelect,
  onDelete,
}: {
  session: ReturnType<typeof useSimulation>["session"];
  names: Names;
  onSelect: (index: number) => void;
  onDelete: (index: number) => void;
}) {
  const rows = traceRows(session.inputs, session.result, names);
  const selected = session.selected ?? session.inputs.length - 1;
  const listRef = useRef<HTMLUListElement>(null);
  const focusRow = (index: number) => {
    requestAnimationFrame(() => listRef.current?.querySelector<HTMLElement>(`[data-index="${index}"]`)?.focus());
  };
  const onKey = (e: KeyboardEvent<HTMLLIElement>, index: number) => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const next = Math.min(rows.length - 2, Math.max(-1, index + (e.key === "ArrowDown" ? 1 : -1)));
      onSelect(next);
      focusRow(next);
    } else if (e.key === "Enter") {
      e.preventDefault();
      onSelect(index);
    } else if (e.key === "Delete") {
      e.preventDefault();
      onDelete(index);
      focusRow(Math.max(-1, index - 1));
    }
  };
  return (
    <div>
      <ul ref={listRef} aria-label={L.trace}>
        {rows.map((r) => (
          <li
            key={r.index}
            tabIndex={0}
            aria-current={r.index === selected ? "step" : undefined}
            className={cn(
              ROW,
              "cursor-default focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-accent",
              r.index === selected && "bg-accent-subtle",
              !r.ran && "text-disabled",
            )}
            onClick={() => onSelect(r.index)}
            onKeyDown={(e) => onKey(e, r.index)}
            title={r.states ? `${r.label} → ${r.states}` : r.label}
            data-testid="trace-row"
            data-index={r.index}
            data-accepted={r.ran ? String(r.accepted) : undefined}
          >
            <span className="w-4 shrink-0 text-right text-11 text-secondary">{r.index >= 0 ? r.index + 1 : ""}</span>
            <span className="min-w-[7rem] flex-[3] basis-auto truncate" title={r.label}>
              {r.label}
            </span>
            {!r.ran ? (
              <span className="text-11">{L.notRun}</span>
            ) : !r.accepted ? (
              <Badge
                tone="danger"
                title={
                  r.refusedBy ? RL.refusedByLine(L.refusals[r.refusal ?? ""] ?? r.refusal ?? "", r.refusedBy) : (L.refusals[r.refusal ?? ""] ?? r.refusal ?? "")
                }
              >
                {r.refusal}
              </Badge>
            ) : (
              <span className="max-w-[45%] truncate text-11 text-secondary" title={r.states}>
                {r.states}
              </span>
            )}
            {r.index >= 0 ? (
              <Button
                variant="ghost"
                size="icon-row"
                tabIndex={-1}
                label={L.deleteInput(r.label)}
                shortcut="Delete"
                onClick={(e) => {
                  e.stopPropagation();
                  onDelete(r.index);
                }}
                data-testid="trace-delete"
              >
                <Trash2 />
              </Button>
            ) : null}
          </li>
        ))}
      </ul>
      {session.inputs.length ? (
        <p className="truncate px-1.5 py-0.5 text-11 text-secondary" title={L.traceHint}>
          {L.traceHint}
        </p>
      ) : (
        <Note>{L.noInputs}</Note>
      )}
    </div>
  );
}

function ReplaySection({ replay, names, onSelect }: { replay: ReplayState; names: Names; onSelect: (index: number) => void }) {
  const rows = replayRows(replay.scenario, replay.verification, replay.simulation, names);
  const failure = replay.verification.failure;
  return (
    <div data-testid="replay-panel" data-passed={replay.verification.passed ? "true" : "false"}>
      <Note testid="replay-summary" wrap tone={replay.verification.passed ? "secondary" : "danger"}>
        {replaySummary(replay.verification, rows.length)}
      </Note>
      <ul aria-label={L.replay}>
        {rows.map((r) => (
          <li
            key={r.index}
            tabIndex={0}
            aria-current={r.index === replay.selected ? "step" : undefined}
            className={cn(
              ROW,
              "cursor-default focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-accent",
              r.index === replay.selected && "bg-accent-subtle",
            )}
            onClick={() => onSelect(r.index)}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown" || e.key === "ArrowUp") {
                e.preventDefault();
                onSelect(Math.min(rows.length - 1, Math.max(0, r.index + (e.key === "ArrowDown" ? 1 : -1))));
              }
            }}
            title={`${r.label}: ${r.verdict}${r.states ? ` → ${r.states}` : ""}`}
            data-testid="replay-row"
            data-verdict={r.verdict}
          >
            <span className="w-4 shrink-0 text-right text-11 text-secondary">{r.index + 1}</span>
            <span className="min-w-[7rem] flex-1 truncate" title={r.label}>
              {r.label}
            </span>
            <Badge tone={r.verdict === "passed" ? "success" : r.verdict === "failed" ? "danger" : "neutral"} data-testid="replay-verdict">
              {r.verdict === "passed" ? L.replayPassed : r.verdict === "failed" ? L.replayFailed : L.replayNotRun}
            </Badge>
            <span className="min-w-0 flex-[2] basis-auto truncate text-right text-11 text-secondary" title={r.states}>
              {r.states}
            </span>
          </li>
        ))}
      </ul>
      {failure ? (
        <div className="grid grid-cols-2 divide-x divide-default border-b border-default" data-testid="replay-diff">
          {(
            [
              [L.expected, failure.expected, "replay-expected-row"],
              [L.actual, failure.actual, "replay-actual-row"],
            ] as const
          ).map(([title, value, testid]) => (
            <ul key={testid} aria-label={title}>
              <li className={cn(ROW, "font-semibold text-secondary")}>{title}</li>
              {failureLines(value, names).map((line, i) => (
                <li key={i} className={ROW} data-testid={testid} title={line}>
                  <span className="truncate">{line}</span>
                </li>
              ))}
            </ul>
          ))}
        </div>
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ dialogs

function RecordDialog({
  initialName,
  initialOutcome,
  onRecord,
  onClose,
}: {
  initialName: string;
  initialOutcome: "final" | "active";
  onRecord: (name: string, outcome: "final" | "active") => Promise<string | null>;
  onClose: () => void;
}) {
  const [name, setName] = useState(initialName);
  const [outcome, setOutcome] = useState(initialOutcome);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const valid = /^[A-Za-z_][A-Za-z0-9_]*$/.test(name.trim());
  const submit = async () => {
    if (!valid || busy) return;
    setBusy(true);
    setError(null);
    const failed = await onRecord(name.trim(), outcome);
    setBusy(false);
    if (failed) setError(failed);
    else onClose();
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={L.recordTitle}>
        <form
          className="flex flex-col gap-2"
          data-testid="record-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void submit();
          }}
        >
          <Field label={L.recordName} htmlFor="record-name">
            <Input id="record-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} aria-invalid={name !== "" && !valid} />
          </Field>
          <Field label={L.recordOutcome} htmlFor="record-outcome">
            <Select id="record-outcome" value={outcome} onChange={(e) => setOutcome(e.target.value as "final" | "active")}>
              <option value="active">{L.outcomeActive}</option>
              <option value="final">{L.outcomeFinal}</option>
            </Select>
          </Field>
          {error ? <p className="text-12 text-danger">{error}</p> : null}
          <div className="flex justify-end gap-2">
            <Button variant="secondary" size="sm" onClick={onClose}>
              {L.cancel}
            </Button>
            <Button type="submit" variant="primary" size="sm" disabled={!valid || busy} data-testid="record-save">
              {L.recordSave}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function ReplayDialog({
  scenarios,
  busy,
  onReplay,
  onClose,
}: {
  scenarios: { id: string; name: string; displayName?: string }[];
  busy: boolean;
  onReplay: (scenario: string) => Promise<string | null>;
  onClose: () => void;
}) {
  const [scenario, setScenario] = useState(scenarios[0]?.id ?? "");
  const [error, setError] = useState<string | null>(null);
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={L.replayTitle}>
        <form
          className="flex flex-col gap-2"
          data-testid="replay-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            if (!scenario || busy) return;
            void onReplay(scenario).then(setError);
          }}
        >
          {scenarios.length ? (
            <Field label={L.replayPick} htmlFor="replay-scenario">
              <Select id="replay-scenario" autoFocus value={scenario} onChange={(e) => setScenario(e.target.value)}>
                {scenarios.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.displayName || s.name}
                  </option>
                ))}
              </Select>
            </Field>
          ) : (
            <p className="text-12 text-secondary">{L.noScenarios}</p>
          )}
          {error ? <p className="text-12 text-danger">{error}</p> : null}
          <div className="flex justify-end gap-2">
            <Button variant="secondary" size="sm" onClick={onClose}>
              {L.cancel}
            </Button>
            <Button type="submit" variant="primary" size="sm" disabled={!scenario || busy} data-testid="replay-run">
              <CirclePlay />
              {L.replayRun}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
