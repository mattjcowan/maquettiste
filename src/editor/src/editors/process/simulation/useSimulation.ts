// Runs the simulation session against the engine (phase-3-design.md 6.4): each change of the start, the inputs or the
// process calls POST /api/processes/{id}/simulate with the whole input list, 150 ms after the last change, and aborts
// the call still in flight, so only the answer for the newest list is shown. The draft document is sent while the
// process has unsaved edits. After every answer the canvas's view (view.ts) is published.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useServices } from "@/app/context";
import { useElement } from "@/api/queries";
import { useEditor } from "@/state/store";
import { simulateProcess, type SimulateOutcome, type SimulateRequest } from "./api";
import { newSession, requestOf, viewOf, withAnswer, type Session } from "./session";
import { setSimulationView } from "./view";

export const SIMULATE_DEBOUNCE_MS = 150;

export interface RunnerDeps {
  call: (body: SimulateRequest, signal: AbortSignal) => Promise<SimulateOutcome>;
  /** The answer, with the tag the call was scheduled with (the session's version). */
  onOutcome: (outcome: SimulateOutcome, tag: number) => void;
  onError: (error: Error, tag: number) => void;
  /** Called when a call starts (true) and when the newest one settles (false). */
  onBusy?: (busy: boolean) => void;
  delay?: number;
  setTimer?: (fn: () => void, ms: number) => unknown;
  clearTimer?: (handle: unknown) => void;
}

/**
 * The debounce and abort of the simulate calls, apart from React so it can be tested with fake timers: `schedule`
 * restarts the delay and aborts the call in flight; an aborted call's answer (or failure) is never reported.
 */
export class SimulationRunner {
  private timer: unknown = null;
  private controller: AbortController | null = null;
  private readonly delay: number;
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;

  constructor(private readonly deps: RunnerDeps) {
    this.delay = deps.delay ?? SIMULATE_DEBOUNCE_MS;
    this.setTimer = deps.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
    this.clearTimer = deps.clearTimer ?? ((h) => clearTimeout(h as ReturnType<typeof setTimeout>));
  }

  schedule(body: SimulateRequest, tag = 0): void {
    this.cancel();
    this.timer = this.setTimer(() => {
      this.timer = null;
      void this.fire(body, tag);
    }, this.delay);
  }

  /** Drops the pending call and aborts the one in flight. */
  cancel(): void {
    if (this.timer !== null) this.clearTimer(this.timer);
    this.timer = null;
    if (this.controller) {
      this.controller.abort();
      this.controller = null;
    }
  }

  private async fire(body: SimulateRequest, tag: number): Promise<void> {
    const controller = new AbortController();
    this.controller = controller;
    this.deps.onBusy?.(true);
    try {
      const outcome = await this.deps.call(body, controller.signal);
      if (!controller.signal.aborted) this.deps.onOutcome(outcome, tag);
    } catch (e) {
      if (!controller.signal.aborted) this.deps.onError(e instanceof Error ? e : new Error(String(e)), tag);
    } finally {
      if (this.controller === controller) {
        this.controller = null;
        this.deps.onBusy?.(false);
      }
    }
  }
}

// The session of each process outlives its panel for the page's life, so leaving the Chart tab (or the editor) and
// coming back finds the inputs where they were; the engine replays them on return.
const kept = new Map<string, Session>();

/** The session of one process's panel and the engine's answers for it. */
export function useSimulation(process: string) {
  const { store } = useServices();
  const [session, setSession] = useState<Session>(() => kept.get(process) ?? newSession(process));
  useEffect(() => {
    kept.set(process, session);
  }, [process, session]);
  const [busy, setBusy] = useState(false);
  // The draft while the process has unsaved edits; the saved document's hash otherwise (a save elsewhere re-runs).
  const draft = useEditor(store, (s) => s.drafts[process]?.json);
  const savedHash = useElement(process).data?.hash;
  const runner = useRef<SimulationRunner | null>(null);
  runner.current ??= new SimulationRunner({
    call: (body, signal) => simulateProcess(process, body, signal),
    // An answer asked for an older version of the inputs is dropped (it would show for one round trip).
    onOutcome: (outcome, tag) => setSession((s) => withAnswer(s, tag, outcome)),
    onError: (error, tag) => setSession((s) => withAnswer(s, tag, { kind: "error", message: error.message })),
    onBusy: setBusy,
  });

  const { start, inputs, version } = session;
  const request = useMemo(() => requestOf({ ...newSession(process), start, inputs }, draft), [process, start, inputs, draft]);
  useEffect(() => {
    runner.current?.schedule(request, version);
  }, [request, savedHash, version]);
  useEffect(() => () => runner.current?.cancel(), []);

  // The canvas follows the entry shown: after every answer, and when another trace entry is selected.
  const view = viewOf(session);
  const viewKey = view ? `${view.step}|${view.configuration.join(",")}|${view.taken.join(",")}|${session.result?.processHash ?? ""}` : "";
  const viewRef = useRef(view);
  viewRef.current = view;
  useEffect(() => {
    if (viewRef.current) setSimulationView(viewRef.current);
  }, [viewKey, session.result]);
  useEffect(() => () => setSimulationView(null), []);

  const update = useCallback((change: (s: Session) => Session) => setSession(change), []);
  /** Shows the session's view on the canvas again (after a replay showed its own). */
  const publish = useCallback(() => {
    if (viewRef.current) setSimulationView(viewRef.current);
  }, []);
  return { session, update, publish, busy: busy || (!session.result && !session.invalid && !session.error) };
}
