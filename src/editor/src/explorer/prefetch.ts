// Reading documents ahead (explorer-redesign.md 3.5, 4.2): a row the pointer or the tree's focus rests on for 300 ms
// has its document read, so opening it is instant; the General-mode editor reads the rows beside the followed one.
import { useEffect, useMemo, useRef } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { PREFETCH_DELAY_MS, prefetchElement } from "@/api/queries";

/** A timer that reads one element's document after the delay unless cancelled first. */
export function useDelayedPrefetch(delay = PREFETCH_DELAY_MS) {
  const qc = useQueryClient();
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const handle = useMemo(
    () => ({
      start(id: string) {
        clearTimeout(timer.current);
        timer.current = setTimeout(() => prefetchElement(qc, id), delay);
      },
      cancel() {
        clearTimeout(timer.current);
        timer.current = undefined;
      },
    }),
    [qc, delay],
  );
  useEffect(() => handle.cancel, [handle]);
  return handle;
}
