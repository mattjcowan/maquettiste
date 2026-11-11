// "Fired by" (processes-operations-design.md section 12): the operations that fire an event, read from the event's
// references while a tab is open (one small call per event, cached by the query layer) and named from the index.
import { useReferencesOf, useIndex } from "@/api/queries";
import type { ElementSummary } from "@/api/types";
import { useEditorNavigation } from "@/app/navigation";
import { indexLookup } from "@/model/index";
import { displayName } from "@/model/model";
import { firedByIds } from "@/model/operationLinks";

/** The operations that fire each event (by event id), as index rows; an operation the index does not know is left out. */
export function useFiredBy(eventIds: string[]): Map<string, ElementSummary[]> {
  const references = useReferencesOf(eventIds);
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const out = new Map<string, ElementSummary[]>();
  for (const id of eventIds) {
    const rows = firedByIds(references.get(id)).flatMap((o) => lookup.byId.get(o) ?? []);
    out.set(id, rows);
  }
  return out;
}

/** Operation names as links: a click opens the operation. */
export function OperationLinks({ operations, testid = "fired-by" }: { operations: ElementSummary[]; testid?: string }) {
  const { openEditor } = useEditorNavigation();
  if (!operations.length) return null;
  return (
    <span className="flex min-w-0 flex-wrap gap-x-1" data-testid={testid}>
      {operations.map((o, i) => (
        <span key={o.id}>
          <button
            type="button"
            className="text-accent underline-offset-2 hover:underline"
            title={`Open ${displayName(o)}`}
            onClick={(e) => {
              e.stopPropagation();
              openEditor(o, true);
            }}
          >
            {displayName(o)}
          </button>
          {i < operations.length - 1 ? "," : ""}
        </span>
      ))}
    </span>
  );
}
