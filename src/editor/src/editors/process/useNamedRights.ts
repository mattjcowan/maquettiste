// The project's named rights (kind `right`, rights-design.md decision 7) as the pickers list them: id and name, in name order.
import { useMemo } from "react";
import { useIndex } from "@/api/queries";
import { indexLookup } from "@/model/index";

export function useNamedRights(): { id: string; name: string }[] {
  const index = useIndex();
  return useMemo(
    () =>
      indexLookup(index.data)
        .ofKind("right")
        .map((r) => ({ id: r.id, name: r.displayName || r.name }))
        .sort((a, b) => a.name.localeCompare(b.name)),
    [index.data],
  );
}
