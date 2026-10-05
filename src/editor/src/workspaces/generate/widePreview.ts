// Previews that render more than one element (generation-ui.md 5.2, "Bounds"; the owner: "if I'm not clicking generate
// somewhere, you should not be generating the full model ever"): a unit whose one render covers the whole model, a
// database or a locale, and a list of elements the user picks. Nothing renders until the user asks. After that, a
// change to what it renders (unsaved template text, the model) renders again by itself only when the last render was
// fast; otherwise the result stays up, marked out of date, until the user asks again.
import { useCallback, useEffect, useRef, useState } from "react";

/** A render at most this long keeps refreshing by itself after the user asked for it once. */
export const FAST_MS = 1000;
/** How long after the last change a fast render refreshes. */
const REFRESH_DELAY = 300;

export interface AskedPreview<T> {
  /** The last result for the current target, or null before the user asked. */
  result: T | null;
  error: string | null;
  /** A render is in flight. */
  pending: boolean;
  /** The user asked for this target in this session. */
  asked: boolean;
  /** The inputs changed since the result rendered, and the result was too slow to refresh by itself. */
  stale: boolean;
  /** How long the last render took, in ms. */
  elapsedMs: number | null;
  /** Renders the current target now (the Preview and Preview again buttons). */
  run(): void;
  /** Renders `target` as soon as it is the current one (a list just picked becomes the target on the next render). */
  runWhenReady(target: string): void;
}

/**
 * One explicitly asked preview. `target` names what is rendered (pack, unit, element or list) and `inputs` what can
 * change it (unsaved text); `render` does the work and reports how long it took. A new target forgets the old one.
 */
export function useAskedPreview<T>(
  target: string | null,
  inputs: string,
  render: (signal: AbortSignal) => Promise<{ value: T; elapsedMs: number }>,
): AskedPreview<T> {
  const [asked, setAsked] = useState<string | null>(null);
  const [wanted, setWanted] = useState<{ target: string; at: number } | null>(null);
  const [last, setLast] = useState<{ target: string; inputs: string; value: T | null; error: string | null; elapsedMs: number } | null>(null);
  const [pending, setPending] = useState(false);
  const renderRef = useRef(render);
  const inputsRef = useRef(inputs);
  useEffect(() => {
    renderRef.current = render;
    inputsRef.current = inputs;
  });
  const controller = useRef<AbortController | null>(null);

  const go = useCallback((what: string) => {
    controller.current?.abort();
    const c = new AbortController();
    controller.current = c;
    const at = inputsRef.current;
    const started = performance.now();
    setPending(true);
    renderRef
      .current(c.signal)
      .then(
        ({ value, elapsedMs }) => setLast({ target: what, inputs: at, value, error: null, elapsedMs }),
        (e: unknown) => {
          if (c.signal.aborted) return;
          setLast({ target: what, inputs: at, value: null, error: (e as Error)?.message ?? String(e), elapsedMs: performance.now() - started });
        },
      )
      .finally(() => {
        if (controller.current === c) {
          controller.current = null;
          setPending(false);
        }
      });
  }, []);

  useEffect(() => () => controller.current?.abort(), []);

  const run = useCallback(() => {
    if (!target) return;
    setAsked(target);
    go(target);
  }, [target, go]);

  const runWhenReady = useCallback((next: string) => setWanted({ target: next, at: performance.now() }), []);
  useEffect(() => {
    if (!wanted || wanted.target !== target) return;
    setWanted(null);
    setAsked(target);
    go(target);
  }, [wanted, target, go]);

  const isAsked = !!target && asked === target;
  const current = last && last.target === target ? last : null;
  const changed = !!current && current.inputs !== inputs;
  const fast = !!current && !current.error && current.elapsedMs <= FAST_MS;
  // A fast render follows the inputs by itself (debounced); a slow one waits to be asked again.
  useEffect(() => {
    if (!isAsked || !changed || !fast || pending || !target) return;
    const t = setTimeout(() => go(target), REFRESH_DELAY);
    return () => clearTimeout(t);
  }, [isAsked, changed, fast, pending, target, inputs, go]);

  return {
    result: current?.value ?? null,
    error: current?.error ?? null,
    pending,
    asked: isAsked,
    stale: isAsked && changed && !fast && !pending,
    elapsedMs: current?.elapsedMs ?? null,
    run,
    runWhenReady,
  };
}
