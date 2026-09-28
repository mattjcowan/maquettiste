import { useMemo } from "react";
import { CircleAlert, Info, TriangleAlert } from "lucide-react";
import { useIndex, useValidation } from "@/api/queries";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { useEditor } from "@/state/store";
import { indexLookup } from "@/model/index";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { groupProblems } from "./group";

const ICON = { error: CircleAlert, warning: TriangleAlert, info: Info } as const;
const TONE = { error: "text-danger", warning: "text-warning", info: "text-accent" } as const;

export function useProblemGroups() {
  const { store } = useServices();
  const validation = useValidation();
  const index = useIndex();
  const drafts = useEditor(store, (s) => s.drafts);
  const groups = useMemo(
    () => groupProblems(validation.data?.diagnostics ?? [], Object.values(drafts), indexLookup(index.data).byId),
    [validation.data, drafts, index.data],
  );
  return { groups, validation };
}

export function ProblemsPanel() {
  const { groups, validation } = useProblemGroups();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const { reveal, select } = useEditorNavigation();
  if (validation.isPending) return <Spinner label="Validating" />;
  if (!groups.length) return <EmptyState title="No problems">The model validates cleanly.</EmptyState>;
  return (
    <ul className="flex flex-col py-1" aria-label="Problems by element" data-testid="problems-list">
      {groups.map((group) => (
        <li key={group.key}>
          <div className="flex h-7 items-center gap-2 px-3 text-12 font-semibold">
            <span>{group.label}</span>
            <span className="font-normal text-secondary">{group.diagnostics.length}</span>
          </div>
          <ul>
            {group.diagnostics.map((d, i) => {
              const Icon = ICON[d.severity];
              return (
                <li key={i}>
                  <button
                    type="button"
                    data-testid={`problem-${d.rule}`}
                    className="flex w-full items-start gap-2 px-6 py-1 text-left text-12 hover:bg-accent-subtle focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent"
                    onClick={() => {
                      const summary = group.elementId ? lookup.byId.get(group.elementId) : undefined;
                      if (summary) reveal(summary, d.jsonPointer);
                      else if (group.elementId) select([group.elementId], d.jsonPointer);
                    }}
                  >
                    <Icon className={`mt-0.5 size-3.5 shrink-0 ${TONE[d.severity]}`} aria-label={d.severity} />
                    <span className="font-mono text-secondary">{d.rule}</span>
                    <span className="flex-1">{d.message}</span>
                    {d.jsonPointer ? <span className="font-mono text-11 text-secondary">{d.jsonPointer}</span> : null}
                  </button>
                </li>
              );
            })}
          </ul>
        </li>
      ))}
    </ul>
  );
}
