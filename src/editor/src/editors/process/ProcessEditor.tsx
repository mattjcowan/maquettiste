// The process editor (phase-3-design.md 6.2) in the editor frame: the top controls, then States, Transitions, Events,
// Gates, Context and Scenarios (the Chart tab arrives with the canvas, round P4). It opens on States, or on the tab a
// row of the Processes explorer asked for with that node selected (app/processFocus.ts).
import { useEffect, useState } from "react";
import { clearProcessFocus, useProcessFocus, type ProcessTab } from "@/app/processFocus";
import { useServices } from "@/app/context";
import { PROCESS_TAB_LABELS } from "@/model/labels";
import { useEditor, type Draft } from "@/state/store";
import { EditorLayout, useEditorContext, type EditorContext } from "../EditorFrame";
import { setView } from "../tabs";
import { ProcessControls, useBinding } from "./ProcessControls";
import { StatesTab } from "./StatesTab";
import { TransitionsTab } from "./TransitionsTab";
import { EventsTab } from "./EventsTab";
import { GatesTab } from "./GatesTab";
import { ScenariosTab } from "./ScenariosTab";
import { ProcessAttributeGrid } from "./AttributeLists";
import { useProcessContext } from "./shared";
import { processSelection, selectProcessNode } from "./selection";

export function ProcessEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "process");
  if (!ctx) return <>{fallback}</>;
  return <ProcessBody ctx={ctx} draft={draft} />;
}

function ProcessBody({ ctx, draft }: { ctx: EditorContext; draft: Draft | undefined }) {
  const { store } = useServices();
  const pc = useProcessContext(ctx);
  const { id, process } = pc;
  const view = useEditor(store, (s) => s.editors.view.process) ?? "states";
  const focus = useProcessFocus(id);
  const [node, setNode] = useState<{ tab: ProcessTab; node: string | null } | null>(null);
  const { members } = useBinding(process.subject, process.boundAttribute);

  // A row of the Processes explorer asked for a tab and a node: show them, then clear the request.
  useEffect(() => {
    if (!focus) return;
    store.getState().updateEditors((e) => setView(e, "process", focus.tab));
    setNode({ tab: focus.tab, node: focus.node });
    if (focus.tab === "states" && focus.node) selectProcessNode({ process: id, kind: "state", id: focus.node });
    clearProcessFocus();
  }, [focus, id, store]);

  // The inspector follows what is selected on the tab shown: leaving States or Transitions leaves their selection.
  useEffect(() => {
    const s = processSelection();
    if (s?.process === id && ((s.kind === "state" && view !== "states") || (s.kind === "transition" && view !== "transitions"))) selectProcessNode(null);
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
          { value: "states", label: PROCESS_TAB_LABELS.states, content: <StatesTab pc={pc} members={members} /> },
          { value: "transitions", label: PROCESS_TAB_LABELS.transitions, content: <TransitionsTab pc={pc} /> },
          { value: "events", label: PROCESS_TAB_LABELS.events, content: <EventsTab pc={pc} focus={nodeOn("events")} /> },
          { value: "gates", label: PROCESS_TAB_LABELS.gates, content: <GatesTab pc={pc} /> },
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
