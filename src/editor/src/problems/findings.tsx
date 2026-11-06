// How an element's findings (diagnostics) show outside the Problems panel: one summary line under an editor's title, and
// the inspector's list of what belongs to the selected part. Severity always shows as it is: a warning is never red.
import { CircleAlert, Info, TriangleAlert } from "lucide-react";
import type { Diagnostic } from "@/api/types";
import { useServices } from "@/app/context";
import { setProblemElement } from "./filter";

export const SEVERITY_ICON = { error: CircleAlert, warning: TriangleAlert, info: Info } as const;
export const SEVERITY_TONE = { error: "text-danger", warning: "text-warning", info: "text-accent" } as const;

type Json = Record<string, unknown>;

/** True when the finding sits on a part of the element (a state, a transition, a subjects entry): at or under an item of a list. */
function isPartPointer(pointer: string | null | undefined, json: unknown): boolean {
  const first = (pointer ?? "").split("/")[1];
  return !!first && Array.isArray((json as Json | undefined)?.[first]);
}

/**
 * The findings for what is selected. A part (`pointer` given): the findings at that pointer or under it. The element
 * itself (`pointer` null): only the findings at its own top level, which belong to no part.
 */
export function findingsFor(diagnostics: readonly Diagnostic[], pointer: string | null, json: unknown): Diagnostic[] {
  if (pointer === null) return diagnostics.filter((d) => !isPartPointer(d.jsonPointer, json));
  return diagnostics.filter((d) => d.jsonPointer === pointer || (d.jsonPointer ?? "").startsWith(`${pointer}/`));
}

/** "1 error · 5 warnings": only the severities that are there; the empty string when there are none. */
export function summaryText(diagnostics: readonly Diagnostic[]): string {
  const count = (s: Diagnostic["severity"]) => diagnostics.filter((d) => d.severity === s).length;
  const part = (n: number, one: string, many: string) => (n ? `${n} ${n === 1 ? one : many}` : null);
  return [part(count("error"), "error", "errors"), part(count("warning"), "warning", "warnings"), part(count("info"), "note", "notes")]
    .filter(Boolean)
    .join(" · ");
}

/** The one line under an editor's title; it opens the Problems panel filtered to this element. */
export function FindingsSummary({ diagnostics, element }: { diagnostics: readonly Diagnostic[]; element: string }) {
  const { store } = useServices();
  const text = summaryText(diagnostics);
  if (!text) return null;
  const open = () => {
    setProblemElement(element);
    store.getState().setBottomTab("problems");
  };
  const severities = (["error", "warning", "info"] as const).filter((s) => diagnostics.some((d) => d.severity === s));
  return (
    <button
      type="button"
      className="flex w-fit items-center gap-1 text-12 hover:underline focus-visible:outline-2 focus-visible:outline-accent"
      title="Show these in the Problems panel"
      data-testid="editor-findings"
      onClick={open}
    >
      {severities.map((s) => {
        const Icon = SEVERITY_ICON[s];
        return <Icon key={s} className={`size-3.5 shrink-0 ${SEVERITY_TONE[s]}`} aria-label={s} />;
      })}
      <span>{text}</span>
    </button>
  );
}

/** The findings of one selection, each with its own severity; nothing when there are none. */
export function FindingsList({ diagnostics, testId = "draft-diagnostics" }: { diagnostics: readonly Diagnostic[]; testId?: string }) {
  if (!diagnostics.length) return null;
  return (
    <ul className="flex flex-col gap-0.5 text-12" data-testid={testId}>
      {diagnostics.map((d, i) => {
        const Icon = SEVERITY_ICON[d.severity];
        return (
          <li key={i} className="flex gap-1" data-severity={d.severity}>
            <Icon className={`mt-0.5 size-3.5 shrink-0 ${SEVERITY_TONE[d.severity]}`} aria-label={d.severity} />
            <span className={d.severity === "error" ? "text-danger" : undefined}>
              <span className="font-mono">{d.rule}</span> {d.message}
            </span>
          </li>
        );
      })}
    </ul>
  );
}
