// The rows the simulation panel shows (phase-3-design.md 6.4), derived from the engine's answer and the process
// document: the enabled triggers with the input each raises by default, the timers, the pending invokes, the active
// states by path, the lines of the last step, the trace rows and a replay's rows. Everything the rows state about the
// run (configuration, guard results, refusals, gate progress, verdicts) is read from the answer; the document only
// supplies names and the forms' fields. Pure: no React, no I/O.
import type { AttributeDoc } from "@/api/types";
import { SIMULATION_LABELS as L } from "@/model/labels";
import { stateIndex, transitionLabel, type EventDoc, type ProcessDoc, type ScenarioDoc } from "@/model/process";
import type { EnabledTrigger, ScenarioVerification, SimInput, SimulationResult, StepTrace } from "./api";

// ------------------------------------------------------------------ names

export interface Names {
  state(id: string): string;
  event(id: string | null | undefined): string;
  invoke(id: string | null | undefined): string;
  actor(id: string | null | undefined): string;
  guard(id: string): string;
  action(id: string): string;
  transition(id: string): string;
  attribute(id: string): string;
  meaning(id: string | null | undefined): string;
  gate(id: string): string;
}

/** Display names for the ids an answer carries: state paths, event, invoke, actor, guard, action and gate names. */
export function namesOf(process: ProcessDoc, actorNames: ReadonlyMap<string, string>): Names {
  const states = new Map([...stateIndex(process)].map(([id, info]) => [id, info.path]));
  const map = (list: { id: string; name: string }[] | undefined) => new Map((list ?? []).map((x) => [x.id, x.name]));
  const events = map(process.events);
  const guards = map(process.guards);
  const actions = map(process.actions);
  const invokes = new Map<string, string>();
  for (const info of stateIndex(process).values()) for (const i of info.state.invoke ?? []) invokes.set(i.id, i.name);
  const attributes = map(process.context);
  for (const e of process.events ?? []) for (const a of e.payload ?? []) attributes.set(a.id, a.name);
  const meanings = new Map<string, string>();
  const gates = new Map<string, string>();
  const transitions = new Map<string, string>();
  for (const t of process.transitions ?? []) {
    transitions.set(t.id, transitionLabel(process, t));
    if (t.gate) {
      gates.set(t.gate.id, t.gate.displayName || t.gate.name);
      for (const m of t.gate.meanings ?? []) meanings.set(m.id, m.name);
      for (const a of t.gate.auditAttributes ?? []) attributes.set(a.id, a.name);
    }
  }
  const of = (m: Map<string, string>) => (id: string | null | undefined) => (id ? (m.get(id) ?? id) : "");
  return {
    state: (id) => states.get(id) ?? id,
    event: of(events),
    invoke: of(invokes),
    actor: (id) => (id ? (actorNames.get(id) ?? id) : ""),
    guard: (id) => guards.get(id) ?? id,
    action: (id) => actions.get(id) ?? id,
    transition: (id) => transitions.get(id) ?? id,
    attribute: (id) => attributes.get(id) ?? id,
    meaning: of(meanings),
    gate: (id) => gates.get(id) ?? id,
  };
}

/** A value as a short text: strings bare, the rest as JSON. */
export function valueText(v: unknown): string {
  return typeof v === "string" ? v : JSON.stringify(v ?? null);
}

/** A map keyed by attribute id as `name=value, ...`. */
export function changesText(map: Record<string, unknown> | null | undefined, names: Names): string {
  return Object.entries(map ?? {})
    .map(([k, v]) => `${names.attribute(k)}=${valueText(v)}`)
    .join(", ");
}

// ------------------------------------------------------------------ enabled triggers

export interface Option {
  id: string;
  name: string;
}

export interface EnabledRow {
  key: string;
  trigger: EnabledTrigger["trigger"];
  label: string;
  event: string | null;
  invoke: string | null;
  transitions: string[];
  /** The actors the input may come from; empty when any actor may (`anyActor`). */
  actors: Option[];
  anyActor: boolean;
  payload: AttributeDoc[];
  gate: { have: number; need: number; meanings: Option[]; reasonRequired: boolean } | null;
  /** The stub guards (no expression) of the candidate transitions, whose results the input assumes. */
  assume: Option[];
}

export interface RowValue {
  actor: string;
  payload: Record<string, unknown>;
  signer: string;
  meaning: string;
  reason: string;
  assume: Record<string, boolean>;
  after: string;
}

/** One row per enabled trigger of the answer, in the answer's order, with the fields its input needs. */
export function enabledRows(result: SimulationResult, process: ProcessDoc, names: Names, allActors: readonly Option[]): EnabledRow[] {
  const events = new Map((process.events ?? []).map((e) => [e.id, e]));
  const transitions = new Map((process.transitions ?? []).map((t) => [t.id, t]));
  const guards = new Map((process.guards ?? []).map((g) => [g.id, g]));
  return result.enabled.map((e, i) => {
    const event: EventDoc | undefined = e.event ? events.get(e.event) : undefined;
    const candidates = e.transitions.map((id) => transitions.get(id)).filter((t) => !!t);
    const gated = candidates.find((t) => t.gate)?.gate;
    const assume: Option[] = [];
    if (e.guardUnknown)
      for (const t of candidates) {
        const g = t.guard ? guards.get(t.guard) : undefined;
        if (g && !g.expression?.trim() && !assume.some((a) => a.id === g.id)) assume.push({ id: g.id, name: g.name });
      }
    const label =
      e.trigger === "event"
        ? names.event(e.event)
        : e.trigger === "invoke-done"
          ? L.invokeDone(names.invoke(e.invoke))
          : e.trigger === "invoke-error"
            ? L.invokeError(names.invoke(e.invoke))
            : L.advanceTime;
    const actors = e.actors.map((id) => ({ id, name: names.actor(id) }));
    return {
      key: `${e.trigger}:${e.event ?? e.invoke ?? ""}:${i}`,
      trigger: e.trigger,
      label,
      event: e.event,
      invoke: e.invoke,
      transitions: e.transitions,
      actors: actors.length ? actors : allActors.map((a) => ({ ...a })),
      anyActor: actors.length === 0,
      payload: e.trigger === "event" ? (event?.payload ?? []) : [],
      gate:
        e.gate && gated
          ? {
              have: e.gate.have,
              need: e.gate.need,
              meanings: (gated.meanings ?? []).map((m) => ({ id: m.id, name: m.name })),
              reasonRequired: !!gated.reasonRequired,
            }
          : null,
      assume,
    };
  });
}

/** An attribute's default for a form: its `default`, else false for a boolean and empty for the rest. */
export function attributeDefault(a: AttributeDoc): unknown {
  if (a.default !== undefined && a.default !== null) return a.default;
  return builtinType(a) === "bool" ? false : "";
}

export function builtinType(a: AttributeDoc): string {
  return typeof a.type === "string" ? a.type : "ref";
}

const NUMERIC = new Set([
  "int8",
  "int16",
  "int32",
  "int64",
  "uint8",
  "uint16",
  "uint32",
  "uint64",
  "decimal",
  "float32",
  "float64",
  "double",
  "float",
  "money",
]);

/** A form's text as the attribute's value: a number for numeric types (text that is not one stays text). */
export function coerce(a: AttributeDoc, raw: string | boolean): unknown {
  if (typeof raw === "boolean") return raw;
  if (NUMERIC.has(builtinType(a)) && raw.trim() !== "" && Number.isFinite(Number(raw))) return Number(raw);
  return raw;
}

/** The input a row raises with nothing changed: the first allowed actor, the payload defaults, a fresh signer name, the
 * gate's first meaning, every stub guard assumed true, and for time the next timer's delay. */
export function defaultRowValue(row: EnabledRow, result: SimulationResult): RowValue {
  return {
    actor: row.anyActor ? "" : (row.actors[0]?.id ?? ""),
    payload: Object.fromEntries(row.payload.map((a) => [a.id, attributeDefault(a)])),
    signer: row.gate ? `signer${row.gate.have + 1}` : "",
    meaning: row.gate?.meanings[0]?.id ?? "",
    reason: "",
    assume: Object.fromEntries(row.assume.map((g) => [g.id, true])),
    after: row.trigger === "time" ? (nextDue(result)?.after ?? "PT1H") : "",
  };
}

/**
 * One field's edit over a row's earlier edits: the payload and the assumptions merge field by field, so editing one
 * payload field or one assumption keeps the others.
 */
export function mergeRowEdit(previous: Partial<RowValue> | undefined, patch: Partial<RowValue>): Partial<RowValue> {
  const next: Partial<RowValue> = { ...previous, ...patch };
  if (patch.payload) next.payload = { ...previous?.payload, ...patch.payload };
  if (patch.assume) next.assume = { ...previous?.assume, ...patch.assume };
  return next;
}

/** The input a row raises with its current values. A payload field keeps the text typed (so "1." can become "1.5")
 * and is converted to the attribute's type here. */
export function inputOf(row: EnabledRow, value: RowValue): SimInput {
  const input: SimInput = {};
  if (row.trigger !== "event") input.input = row.trigger;
  if (row.event) input.event = row.event;
  if (row.invoke) input.invoke = row.invoke;
  if (row.trigger === "time") input.after = value.after.trim();
  if (value.actor) input.actor = value.actor;
  if (row.gate) {
    if (value.signer.trim()) input.signer = value.signer.trim();
    if (value.meaning) input.meaning = value.meaning;
    if (value.reason.trim()) input.reason = value.reason.trim();
  }
  const payload = Object.fromEntries(
    row.payload
      .map((a) => {
        const v = value.payload[a.id];
        return [a.id, typeof v === "string" ? coerce(a, v) : v] as const;
      })
      .filter(([, v]) => v !== "" && v !== undefined),
  );
  if (Object.keys(payload).length) input.payload = payload;
  const assume = Object.fromEntries(row.assume.map((g) => [g.id, value.assume[g.id] ?? true]));
  if (Object.keys(assume).length) input.assume = assume;
  return input;
}

// ------------------------------------------------------------------ time and pending

/** An ISO 8601 duration for a span in milliseconds (days, hours, minutes, seconds). */
export function durationText(ms: number): string {
  if (ms <= 0) return "PT0S";
  const days = Math.floor(ms / 86_400_000);
  let rest = ms - days * 86_400_000;
  const h = Math.floor(rest / 3_600_000);
  rest -= h * 3_600_000;
  const m = Math.floor(rest / 60_000);
  rest -= m * 60_000;
  const s = rest / 1000;
  const time = `${h ? `${h}H` : ""}${m ? `${m}M` : ""}${s ? `${s}S` : ""}`;
  return `P${days ? `${days}D` : ""}${time ? `T${time}` : ""}`;
}

/** The first timer the answer lists as scheduled, and the delay from the answer's clock to it. */
export function nextDue(result: SimulationResult): { transition: string; dueAt: string; after: string } | null {
  const first = [...result.timers].sort((a, b) => Date.parse(a.dueAt) - Date.parse(b.dueAt))[0];
  if (!first) return null;
  return { transition: first.transition, dueAt: first.dueAt, after: durationText(Date.parse(first.dueAt) - Date.parse(result.clock)) };
}

const DURATION = /^P(?=\d|T\d)(?:\d+Y)?(?:\d+M)?(?:\d+W)?(?:\d+D)?(?:T(?=\d)(?:\d+H)?(?:\d+M)?(?:\d+(?:[.,]\d+)?S)?)?$/;

/** Whether a text has the form of an ISO 8601 duration the engine reads (the Advance field's check; zero is the
 * engine's to refuse). */
export function isDuration(text: string): boolean {
  return DURATION.test(text.trim());
}

export interface PendingRow {
  invoke: string;
  state: string;
  label: string;
  /** The actor a Done or Error sends: the first actor of a human task that names its actors. */
  actor: string | null;
}

export function pendingRows(result: SimulationResult, process: ProcessDoc, names: Names): PendingRow[] {
  const invokes = new Map<string, { actors?: string[]; type?: string }>();
  for (const info of stateIndex(process).values()) for (const i of info.state.invoke ?? []) invokes.set(i.id, i);
  return result.pending.map((p) => {
    const doc = invokes.get(p.invoke);
    return {
      invoke: p.invoke,
      state: p.state,
      label: L.pendingIn(names.invoke(p.invoke), names.state(p.state)),
      actor: doc?.type === "human-task" && doc.actors?.length ? doc.actors[0] : null,
    };
  });
}

// ------------------------------------------------------------------ the last step

export interface LastStepLine {
  kind: "refusal" | "transitions" | "guard" | "action" | "context" | "audit" | "diagnostic" | "incomplete";
  text: string;
  tone: "neutral" | "danger" | "warning" | "success";
}

/** What one trace entry did, line by line, in the order the engine reports it. */
export function lastStepLines(entry: StepTrace, names: Names): LastStepLine[] {
  const lines: LastStepLine[] = [];
  if (entry.refusal) lines.push({ kind: "refusal", text: L.refused(L.refusals[entry.refusal] ?? entry.refusal), tone: "danger" });
  const taken = entry.microsteps.flatMap((m) => m.transitions);
  if (taken.length) lines.push({ kind: "transitions", text: L.transitionLine(taken.map(names.transition).join(", ")), tone: "success" });
  for (const g of entry.guards)
    lines.push({
      kind: "guard",
      text: L.guardLine(
        names.guard(g.guard),
        names.transition(g.transition),
        g.result === null ? L.unknownResult : String(g.result),
        L.guardSources[g.source] ?? g.source,
      ),
      tone: g.source === "missing" ? "warning" : "neutral",
    });
  for (const m of entry.microsteps)
    for (const a of m.actions)
      lines.push({
        kind: "action",
        text: L.actionLine(names.action(a.action), L.actionSources[a.source] ?? a.source, changesText(a.changed, names)),
        tone: "neutral",
      });
  if (Object.keys(entry.changed).length) lines.push({ kind: "context", text: L.changedLine(changesText(entry.changed, names)), tone: "neutral" });
  for (const r of entry.audit)
    lines.push({
      kind: "audit",
      text: L.auditLine(names.gate(r.gate), r.outcome, r.signer ?? "", names.actor(r.actor), names.meaning(r.meaning)),
      tone: r.outcome === "refused" ? "danger" : r.outcome === "completed" ? "success" : "neutral",
    });
  for (const d of entry.diagnostics) lines.push({ kind: "diagnostic", text: `${d.rule} ${d.message}`, tone: d.severity === "error" ? "danger" : "warning" });
  if (entry.incomplete) lines.push({ kind: "incomplete", text: L.incomplete, tone: "warning" });
  return lines;
}

// ------------------------------------------------------------------ the trace

/** An input in a few words: `submit as BudgetHolder`, `checkBudget done`, `time +P5D`. */
export function inputLabel(input: SimInput, names: Names): string {
  const kind = input.input ?? "event";
  const head =
    kind === "time"
      ? L.timeInput(input.after ?? "")
      : kind === "invoke-done"
        ? L.invokeDone(names.invoke(input.invoke))
        : kind === "invoke-error"
          ? L.invokeError(names.invoke(input.invoke))
          : names.event(input.event);
  const parts = [head];
  if (input.actor) parts.push(L.asActor(names.actor(input.actor)));
  if (input.signer) parts.push(L.bySigner(input.signer));
  return parts.join(" ");
}

export interface TraceRow {
  /** The trace index (-1: the start). */
  index: number;
  label: string;
  /** The engine ran it (false after a replay stopped). */
  ran: boolean;
  accepted: boolean;
  refusal: string | null;
  states: string;
}

/** The start, then one row per input, with what the engine answered for it. */
export function traceRows(inputs: readonly SimInput[], result: SimulationResult | null, names: Names): TraceRow[] {
  const byIndex = new Map((result?.trace ?? []).map((t) => [t.index, t]));
  const row = (index: number, label: string): TraceRow => {
    const t = byIndex.get(index);
    return { index, label, ran: !!t, accepted: t?.accepted ?? true, refusal: t?.refusal ?? null, states: (t?.configuration ?? []).map(names.state).join(", ") };
  };
  return [row(-1, L.traceStart), ...inputs.map((input, i) => row(i, inputLabel(input, names)))];
}

/** The active states of an entry, by path. */
export function configurationRows(configuration: readonly string[], names: Names): { id: string; path: string }[] {
  return configuration.map((id) => ({ id, path: names.state(id) }));
}

// ------------------------------------------------------------------ replay

export type Verdict = "passed" | "failed" | "not-run";

export interface ReplayRow {
  index: number;
  label: string;
  verdict: Verdict;
  /** The states after the step, from the engine's simulation of the scenario. */
  states: string;
}

/** A replay's summary line: every step passed, or where it failed; a failure of the outcome (step -1) is the outcome's. */
export function replaySummary(verification: ScenarioVerification, steps: number): string {
  if (verification.passed) return L.replayAllPassed(steps);
  const failure = verification.failure;
  if (!failure) return L.replayFailed;
  return L.replayFailedAt(failure.step < 0 ? L.replayOutcome : L.replayStep(failure.step + 1), failure.rule, failure.message);
}

/** One row per scenario step: the verdict from verify (the engine's), the states after it from simulate. */
export function replayRows(scenario: ScenarioDoc, verification: ScenarioVerification, simulation: SimulationResult | null, names: Names): ReplayRow[] {
  const failedAt = verification.passed ? null : (verification.failure?.step ?? -1);
  const byIndex = new Map((simulation?.trace ?? []).map((t) => [t.index, t]));
  return scenario.steps.map((step, i) => {
    const verdict: Verdict = failedAt === null ? "passed" : failedAt < 0 ? "passed" : i < failedAt ? "passed" : i === failedAt ? "failed" : "not-run";
    const t = byIndex.get(i);
    return {
      index: i,
      label: inputLabel(step as SimInput, names),
      verdict,
      states: t && verdict !== "not-run" ? t.configuration.map(names.state).join(", ") : "",
    };
  });
}

/** A failure's expected or actual value as lines: one state path each, one `name=value` each, or the value itself. */
export function failureLines(value: unknown, names: Names): string[] {
  if (value === null || value === undefined) return ["—"];
  if (Array.isArray(value)) return value.length ? value.map((v) => (typeof v === "string" ? names.state(v) : valueText(v))) : ["(none)"];
  if (typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>);
    return entries.length ? entries.map(([k, v]) => `${names.attribute(k)}=${valueText(v)}`) : ["(none)"];
  }
  return [valueText(value)];
}

/** The same value on one line. */
export function failureText(value: unknown, names: Names): string {
  return failureLines(value, names).join(", ");
}
