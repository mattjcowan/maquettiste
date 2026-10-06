// The snapshot reads: the list (read when the picker opens, never on a timer), a comparison in pages of 500 (the next page
// only on "Load more"), and one element's two documents when it is picked. Each is an explicit action of the person: on a
// model of tens of thousands of documents nothing here runs in the background.
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";

export const COMPARE_PAGE = 500;

export const useSnapshots = (enabled = true) => useQuery({ queryKey: keys.snapshots, queryFn: endpoints.listSnapshots, enabled, staleTime: 0 });

export function useComparison(from: string, to: string) {
  return useInfiniteQuery({
    queryKey: [...keys.snapshotCompare(from, to, 0, COMPARE_PAGE), "pages"],
    queryFn: ({ pageParam }) => endpoints.compareSnapshots(from, to, pageParam, COMPARE_PAGE),
    initialPageParam: 0,
    getNextPageParam: (last) => last.next ?? undefined,
    // Compared once per opening: the working side moves with every edit, so a reopened view compares again.
    gcTime: 0,
  });
}

export function useElementDiff(from: string, to: string, id: string | null) {
  return useQuery({
    queryKey: keys.snapshotElement(from, to, id ?? ""),
    queryFn: () => endpoints.compareSnapshotElement(from, to, id!),
    enabled: !!id,
    gcTime: 0,
  });
}
