// The editor's own timings (explorer-redesign.md 4.5 and 5 item 7). Each stage is a pair of
// performance marks, `mq:<stage>:start` and `mq:<stage>:end`, measured as `mq:<stage>` so the
// browser's performance timeline shows it, and recorded in window.__mqPerf.editor, which the
// Playwright scale project reads next to the mock's own window.__mqPerf.mock. Recording never
// throws: a missing Performance API (tests, old browsers) only loses the numbers.

/** One measured stage. `at` is its start on the page's clock (performance.now()). */
export interface PerfEntry {
  name: string;
  at: number;
  ms: number;
  detail?: Record<string, unknown>;
}

/** window.__mqPerf.editor: every stage in the order it finished, capped at MAX_ENTRIES. */
export interface EditorPerf {
  entries: PerfEntry[];
}

const MAX_ENTRIES = 2000;

function now(): number {
  return typeof performance !== "undefined" ? performance.now() : Date.now();
}

/** The page's record, created on first use. */
export function editorPerf(): EditorPerf {
  const holder = globalThis as unknown as { __mqPerf?: Record<string, unknown> };
  holder.__mqPerf ??= {};
  const current = holder.__mqPerf.editor as EditorPerf | undefined;
  if (current) return current;
  const created: EditorPerf = { entries: [] };
  holder.__mqPerf.editor = created;
  return created;
}

function mark(name: string): void {
  try {
    performance.mark(name);
  } catch {
    /* no Performance API */
  }
}

/** Starts a stage; call the returned function when it ends. */
export function perfStart(stage: string): (detail?: Record<string, unknown>) => PerfEntry {
  const at = now();
  mark(`mq:${stage}:start`);
  let done: PerfEntry | null = null;
  return (detail) => {
    if (done) return done;
    const ms = now() - at;
    mark(`mq:${stage}:end`);
    try {
      performance.measure(`mq:${stage}`, `mq:${stage}:start`, `mq:${stage}:end`);
    } catch {
      /* no Performance API, or the start mark was cleared */
    }
    done = { name: stage, at, ms, ...(detail ? { detail } : {}) };
    const perf = editorPerf();
    perf.entries.push(done);
    if (perf.entries.length > MAX_ENTRIES) perf.entries.splice(0, perf.entries.length - MAX_ENTRIES);
    return done;
  };
}

/** Measures a synchronous stage. */
export function perfSync<T>(stage: string, run: () => T, detail?: (result: T) => Record<string, unknown>): T {
  const end = perfStart(stage);
  const result = run();
  end(detail?.(result));
  return result;
}

/** Records a stage once per page (the first explorer paint, the first index parse). */
const once = new Set<string>();
export function perfOnce(stage: string, run: () => void): void {
  if (once.has(stage)) return;
  once.add(stage);
  run();
}
