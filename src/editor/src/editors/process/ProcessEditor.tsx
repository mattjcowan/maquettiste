// The process editor (phase-3-design.md 6.2) in the editor frame: the top controls, then States, Transitions, Events,
// Gates, Context and Scenarios. The Chart tab (ChartTab.tsx) holds the statechart canvas with the simulation panel
// docked below it. It opens on Chart, or on the tab a row of the Processes explorer asked for with that node selected
// (app/processFocus.ts).
import { useEffect, useState } from "react";
import { clearProcessFocus, useProcessFocus, type ProcessTab } from "@/app/processFocus";
import { useServices } from "@/app/context";
import { EmptyState } from "@/components/ui/misc";
import { LIBRARY_LABELS, PROCESS_TAB_LABELS } from "@/model/labels";
import { soleSubject } from "@/model/process";
import { useEditor, type Draft } from "@/state/store";
import { EditorLayout, useEditorContext, type EditorContext } from "../EditorFrame";
import { setView } from "../tabs";
import { ProcessControls, useBinding, useSubjectMembers } from "./ProcessControls";
import { ChartTab } from "./ChartTab";
import { StatesTab } from "./StatesTab";
import { TransitionsTab } from "./TransitionsTab";
import { EventsTab } from "./EventsTab";
import { GatesTab } from "./GatesTab";
import { RightsTab } from "./RightsTab";
import { ScenariosTab } from "./ScenariosTab";
import { ProcessAttributeGrid } from "./AttributeLists";
import { useProcessContext } from "./shared";
import { processSelection, selectProcessNode } from "./selection";

export function ProcessEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "process");
  if (!ctx) return <>{fallback}</>;
  // A tab left open while the process became a library, or a diagram that names one: a library has no chart to show.
  if ((ctx.json as { use?: unknown }).use === "library") return <EmptyState title={LIBRARY_LABELS.noChartTitle}>{LIBRARY_LABELS.noChartBody}</EmptyState>;
  return <ProcessBody ctx={ctx} draft={draft} />;
}

function ProcessBody({ ctx, draft }: { ctx: EditorContext; draft: Draft | undefined }) {
  const { store } = useServices();
  const pc = useProcessContext(ctx);
  const { id, process } = pc;
  const view = useEditor(store, (s) => s.editors.view.process) ?? "chart";
  const focus = useProcessFocus(id);
  const [node, setNode] = useState<{ tab: ProcessTab; node: string | null } | null>(null);
  const { members, codeKind } = useBinding(soleSubject(process)?.entity, soleSubject(process)?.boundAttribute, soleSubject(process)?.stereotype);
  const subjectMembers = useSubjectMembers(process);

  // A row of the Processes explorer asked for a tab and a node: show them, then clear the request.
  useEffect(() => {
    if (!focus) return;
    store.getState().updateEditors((e) => setView(e, "process", focus.tab));
    setNode({ tab: focus.tab, node: focus.node });
    if ((focus.tab === "states" || focus.tab === "chart") && focus.node) selectProcessNode({ process: id, kind: "state", id: focus.node });
    if (focus.tab === "transitions" && focus.node) selectProcessNode({ process: id, kind: "transition", id: focus.node });
    clearProcessFocus();
  }, [focus, id, store]);

  // The inspector follows what is selected on the tab shown: leaving States or Transitions leaves their selection; the
  // Chart selects both kinds.
  useEffect(() => {
    const s = processSelection();
    if (s?.process !== id || view === "chart") return;
    if ((s.kind === "state" && view !== "states") || (s.kind === "transition" && view !== "transitions")) selectProcessNode(null);
  }, [view, id]);
  useEffect(
    () => () => {
      if (processSelection()?.process === id) selectProcessNode(null);
    },
    [id],
  );

  const nodeOn = (tab: ProcessTab) => (node?.tab === tab ? node.node : null);
  return (
    <div className="h-full" data-testid="process-editor" data-id={id}>
      <EditorLayout
        ctx={ctx}
        draft={draft}
        controls={<ProcessControls ctx={ctx} />}
        tabs={[
          { value: "chart", label: PROCESS_TAB_LABELS.chart, content: <ChartTab pc={pc} />, fill: true },
          {
            value: "states",
            label: PROCESS_TAB_LABELS.states,
            content: <StatesTab pc={pc} members={members} codeKind={codeKind} subjectMembers={subjectMembers} />,
          },
          { value: "transitions", label: PROCESS_TAB_LABELS.transitions, content: <TransitionsTab pc={pc} /> },
          { value: "events", label: PROCESS_TAB_LABELS.events, content: <EventsTab pc={pc} focus={nodeOn("events")} /> },
          { value: "gates", label: PROCESS_TAB_LABELS.gates, content: <GatesTab pc={pc} /> },
          // Rights belong to lifecycles only (MQ9901).
          ...(process.use === "lifecycle" ? [{ value: "rights", label: PROCESS_TAB_LABELS.rights, content: <RightsTab pc={pc} /> }] : []),
          {
            value: "context",
            label: PROCESS_TAB_LABELS.context,
            content: (
              <ProcessAttributeGrid
                pc={pc}
                label={`Context of ${process.name}`}
                owner={id}
                pointerBase="/context"
                at={{
                  get: (p) => p.context,
                  set: (p, list) => {
                    if (list) p.context = list;
                    else delete p.context;
                  },
                }}
              />
            ),
          },
          { value: "scenarios", label: PROCESS_TAB_LABELS.scenarios, content: <ScenariosTab process={id} name={process.name} focus={nodeOn("scenarios")} /> },
        ]}
      />
    </div>
  );
}
