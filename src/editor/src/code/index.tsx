// Lazy entry points for Monaco, so the editor bundle loads only when a code view opens.
import { lazy, Suspense, type ComponentProps } from "react";
import { Spinner } from "@/components/ui/misc";

const LazyCode = lazy(() => import("./CodeEditor"));
const LazyDiff = lazy(() => import("./CodeEditor").then((m) => ({ default: m.CodeDiffEditor })));

export function CodeView(props: ComponentProps<typeof LazyCode>) {
  return (
    <Suspense fallback={<Spinner label="Loading editor" />}>
      <LazyCode {...props} />
    </Suspense>
  );
}

export function CodeDiff(props: ComponentProps<typeof LazyDiff>) {
  return (
    <Suspense fallback={<Spinner label="Loading editor" />}>
      <LazyDiff {...props} />
    </Suspense>
  );
}
