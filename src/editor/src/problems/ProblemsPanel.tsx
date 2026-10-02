import { useMemo } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { CircleAlert, Info, RefreshCw, TriangleAlert } from "lucide-react";
import { keys, useIndex, useValidation, useValidationRules } from "@/api/queries";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { useEditor } from "@/state/store";
import { indexLookup } from "@/model/index";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { countBySeverity, groupProblems } from "./group";
import { applyProblemFilter, setProblemFilter, useProblemFilter, type ProblemFilter } from "./filter";
import { hasQuickFix } from "./quickFix";
import { QuickFixButton } from "./QuickFixButton";
import { Button } from "@/components/ui/button";
import { hasEditor, setView } from "@/editors/tabs";
import { vocabularyProblemTarget, type VocabularyProblemTarget } from "@/model/vocabularies";
import { displayName } from "@/model/model";
import type { ElementSummary } from "@/api/types";

function goToLabel(target: VocabularyProblemTarget, byId: ReadonlyMap<string, ElementSummary>): string {
  if (target.type === "settings") return `the global ${target.tab}`;
  if (target.type === "domain") return `${target.tab} of ${displayName(byId.get(target.domain))}`;
  return displayName(byId.get(target.id));
}

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

/** The groups the panel shows after the severity filter, with the counts of every severity and of the hidden ones. */
export function useVisibleProblemGroups() {
  const { groups, validation } = useProblemGroups();
  const filter = useProblemFilter();
  const counts = useMemo(() => countBySeverity(groups), [groups]);
  const visible = useMemo(() => applyProblemFilter(groups, filter), [groups, filter]);
  return { groups: visible.groups, hidden: visible.hidden, counts, filter, validation };
}

const CHIPS: { key: keyof ProblemFilter; label: string; count: keyof ReturnType<typeof countBySeverity>; tone: string }[] = [
  { key: "errors", label: "Errors", count: "errors", tone: "text-danger" },
  { key: "warnings", label: "Warnings", count: "warnings", tone: "text-warning" },
  { key: "infos", label: "Information", count: "infos", tone: "text-accent" },
];

/** The filter chips and Validate again, above the list. */
function ProblemsHeader({ counts, filter, hidden }: { counts: ReturnType<typeof countBySeverity>; filter: ProblemFilter; hidden: number }) {
  const qc = useQueryClient();
  return (
    <div className="flex h-7 shrink-0 items-center gap-1 border-b border-default px-2" data-testid="problems-header">
      {CHIPS.map((c) => (
        <Button
          key={c.key}
          size="sm"
          variant={filter[c.key] ? "secondary" : "ghost"}
          aria-pressed={filter[c.key]}
          label={filter[c.key] ? `Hide ${c.label.toLowerCase()}` : `Show ${c.label.toLowerCase()}`}
          onClick={() => setProblemFilter({ ...filter, [c.key]: !filter[c.key] })}
          data-testid={`problem-filter-${c.key}`}
        >
          <span className={filter[c.key] ? c.tone : "text-secondary"}>{c.label}</span>
          <span className="text-secondary">{counts[c.count]}</span>
        </Button>
      ))}
      {hidden ? (
        <span className="text-11 text-secondary" data-testid="problems-hidden">
          {hidden} hidden
        </span>
      ) : null}
      <Button
        size="sm"
        variant="ghost"
        className="ml-auto"
        label="Validate again: run every rule over the whole model now"
        onClick={() => void qc.invalidateQueries({ queryKey: keys.validation, exact: true })}
        data-testid="problems-validate"
      >
        <RefreshCw /> Validate again
      </Button>
    </div>
  );
}

export function ProblemsPanel() {
  const { groups, hidden, counts, filter, validation } = useVisibleProblemGroups();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const { reveal, select, openEditor, openSettings } = useEditorNavigation();
  const { store } = useServices();
  const rules = useValidationRules();
  const fixes = useMemo(() => new Map((rules.data ?? []).map((r) => [r.id, r.quickFix ?? null])), [rules.data]);
  /** MQ2008 and MQ3021 (explorer-redesign.md 1.11): the element's marks in its editor, or the vocabulary's own tab. */
  const goToVocabulary = (target: NonNullable<ReturnType<typeof vocabularyProblemTarget>>) => {
    if (target.type === "settings") return openSettings(target.tab);
    const id = target.type === "element" ? target.id : target.domain;
    const summary = lookup.byId.get(id);
    if (!summary) return;
    if (target.type === "domain") store.getState().updateEditors((e) => setView(e, "package", target.tab));
    if (hasEditor(summary.kind, summary)) openEditor(summary, true);
    else reveal(summary);
  };
  if (validation.isPending) return <Spinner label="Validating" />;
  const total = counts.errors + counts.warnings + counts.infos;
  return (
    <div className="flex h-full min-h-0 flex-col">
      <ProblemsHeader counts={counts} filter={filter} hidden={hidden} />
      {!total ? (
        <EmptyState title="No problems">The model validates cleanly.</EmptyState>
      ) : !groups.length ? (
        <EmptyState title="No problems shown">{`${hidden} ${hidden === 1 ? "finding is" : "findings are"} hidden by the filter above.`}</EmptyState>
      ) : (
        <ul className="flex min-h-0 flex-1 flex-col overflow-auto py-1" aria-label="Problems by element" data-testid="problems-list">
          {groups.map((group) => (
            <li key={group.key}>
              <div className="flex h-7 items-center gap-2 px-2 text-12 font-semibold">
                <span>{group.label}</span>
                <span className="font-normal text-secondary">{group.diagnostics.length}</span>
              </div>
              <ul>
                {group.diagnostics.map((d, i) => {
                  const Icon = ICON[d.severity];
                  const target = vocabularyProblemTarget(d.rule, group.elementId, index.data ?? []);
                  return (
                    <li key={i} className="flex items-start">
                      <button
                        type="button"
                        data-testid={`problem-${d.rule}`}
                        className="flex min-w-0 flex-1 items-start gap-2 px-2 py-1 text-left text-12 hover:bg-accent-subtle focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent"
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
                      {!group.unsaved && hasQuickFix(d.rule, fixes.get(d.rule)) ? <QuickFixButton diagnostic={d} catalogFix={fixes.get(d.rule)} /> : null}
                      {target ? (
                        <Button
                          size="sm"
                          variant="ghost"
                          className="mr-2 shrink-0"
                          data-testid={`problem-goto-${d.rule}`}
                          aria-label={`Go to ${goToLabel(target, lookup.byId)}`}
                          onClick={() => goToVocabulary(target)}
                        >
                          Go to
                        </Button>
                      ) : null}
                    </li>
                  );
                })}
              </ul>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
