// The revision levels of the types a lifecycle governs, for the transition inspector, the Transitions grid and the chart's labels.
import { useMemo } from "react";
import { useElements, useIndex } from "@/api/queries";
import { indexLookup } from "@/model/index";
import { levelLabels, type LevelLabel } from "@/model/revisionLabels";
import { resolveRevisions, type EntityWithRevisions } from "@/model/revisionSequence";

/** The level labels for a process: none for an orchestration, a library, or a lifecycle that governs no type with revisions. */
export function useRevisionLevelLabels(processId: string): LevelLabel[] {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const row = lookup.byId.get(processId);
  const governed = useMemo(
    () => (row?.use === "lifecycle" ? [...new Set([...(row.subject ? [row.subject] : []), ...(row.subjects ?? []), ...(row.covers ?? [])])] : []),
    [row],
  );
  const family = useMemo(() => {
    const ids = new Set<string>();
    for (const id of governed) for (let at: string | undefined = id; at && !ids.has(at); at = lookup.byId.get(at)?.base) ids.add(at);
    return [...ids];
  }, [governed, lookup]);
  const docs = useElements(family);
  const docOf = (id: string) => docs.byId.get(id)?.json as EntityWithRevisions;
  const labels = levelLabels(governed.map((id) => resolveRevisions(id, lookup.byId, docOf)?.levels ?? []));
  // The same labels are the same array: the chart and the grids rebuild only when a label changes (the query results are new objects each render).
  const key = JSON.stringify(labels);
  return useMemo(() => JSON.parse(key) as LevelLabel[], [key]);
}
