// The States tab (phase-3-design.md 6.2): the state tree as a grid: name (indented by depth), type, initial, history,
// entry, exit, invokes, and the bound enum member (read-only) of a lifecycle's root states. Ctrl+Enter adds a sibling,
// Ctrl+Shift+Enter a child, Ctrl+Delete deletes (refused while a transition enters the state). Below it, the selected
// state's Invokes grid: name, type, the invoked process and the human task's actors.
import { useState } from "react";
import { ListTree } from "lucide-react";
import { Button } from "@/components/ui/button";
import { SectionTitle } from "@/components/ui/misc";
import { useIndex } from "@/api/queries";
import { newId } from "@/lib/ids";
import { indexLookup } from "@/model/index";
import { CHART_LABELS } from "@/model/labels";
import {
  addSiblingState,
  addState,
  deleteState,
  idsOfNames,
  INVOKE_TYPES,
  setInitial,
  setStateType,
  stateIndex,
  stateRows,
  STATE_TYPES,
  subtreeIds,
  type InvokeDoc,
  type ProcessDoc,
  type StateRow,
  type StateType,
  uniqueName,
} from "@/model/process";
import { useServices } from "@/app/context";
import { changeWithDiagram } from "@/canvas/statechart/actions";
import { processDiagramId } from "@/canvas/statechart/diagram";
import { Grid, type GridColumn } from "./Grid";
import { rowProblems, useGridTargets, type ProcessContext } from "./shared";
import { selectProcessNode, useProcessSelection } from "./selection";

export function StatesTab({ pc, members }: { pc: ProcessContext; members: string[] | null }) {
  const { process, change, id } = pc;
  const services = useServices();
  const index = useIndex();
  const diagramId = processDiagramId(indexLookup(index.data).rows, id);
  const selection = useProcessSelection(id);
  const [error, setError] = useState<string | null>(null);
  const rows = stateRows(process, members);
  const actions = (process.actions ?? []).map((a) => ({ id: a.id, name: a.name }));
  const lifecycle = process.use === "lifecycle";
  const selected = selection?.kind === "state" ? selection.id : null;
  const select = (state: string) => selectProcessNode({ process: id, kind: "state", id: state });
  const go = useGridTargets(id);
  const first = (r: StateRow, key: "entry" | "exit") => stateIndex(process).get(r.id)?.state[key]?.[0];

  const columns: GridColumn<StateRow>[] = [
    { key: "name", label: "Name", kind: "text", width: "min-w-40", value: (r) => r.name },
    {
      key: "type",
      label: "Type",
      kind: "select",
      width: "w-24",
      value: (r) => r.type,
      options: () => STATE_TYPES.map((t) => ({ value: t, label: t })),
    },
    {
      key: "initial",
      label: "Initial",
      kind: "toggle",
      width: "w-12",
      value: (r) => (r.initial ? "yes" : ""),
      on: (r) => r.initial,
      locked: (r) => r.type === "history",
    },
    {
      key: "history",
      label: "History",
      kind: "select",
      width: "w-20",
      value: (r) => r.history,
      locked: (r) => r.type !== "history",
      options: () => [
        { value: "shallow", label: "shallow" },
        { value: "deep", label: "deep" },
      ],
    },
    {
      key: "entry",
      label: "Entry",
      kind: "multi",
      width: "min-w-24",
      value: (r) => r.entry,
      definition: (r) => go.node("transitions", first(r, "entry")),
      options: () => actions.map((a) => ({ value: a.id, label: a.name })),
    },
    {
      key: "exit",
      label: "Exit",
      kind: "multi",
      width: "min-w-24",
      value: (r) => r.exit,
      definition: (r) => go.node("transitions", first(r, "exit")),
      options: () => actions.map((a) => ({ value: a.id, label: a.name })),
    },
    { key: "invokes", label: "Invokes", kind: "readonly", width: "min-w-24", value: (r) => r.invokes },
  ];
  if (lifecycle)
    columns.push({
      key: "bound",
      label: "Bound member",
      kind: "readonly",
      width: "min-w-24",
      value: (r) => r.bound,
      marker: (r) => (r.drift ? { rule: "MQ9203", message: `The enum's member here is ${r.bound || "missing"}; sync the enum.` } : null),
    });

  const commit = (row: StateRow, key: string, value: string | boolean) => {
    setError(null);
    change((p) => {
      const state = stateIndex(p).get(row.id)?.state;
      if (!state) return;
      if (key === "name") state.name = String(value).trim() || state.name;
      else if (key === "type") setStateType(state, value as StateType);
      else if (key === "initial") setInitial(p, row.id);
      else if (key === "history") {
        if (value === "deep") state.history = "deep";
        else delete state.history;
      } else if (key === "entry" || key === "exit") {
        const ids = idsOfNames(String(value), actions);
        if (ids.length) state[key] = ids;
        else delete state[key];
      }
    });
  };

  const add = (after: StateRow | undefined, child: boolean) => {
    setError(null);
    const created: string[] = [];
    change((p) => void created.push(child && after ? addState(p, after.id) : after ? addSiblingState(p, after.id) : addState(p, null)));
    if (created[0]) select(created[0]);
  };

  return (
    <div className="flex flex-col gap-2">
      {error ? (
        <p role="alert" className="text-12 text-danger" data-testid="states-error">
          {error}
        </p>
      ) : null}
      <Grid
        label={`States of ${process.name}`}
        testid="states-grid"
        noun="state"
        rows={rows}
        columns={columns}
        rowName={(r) => r.path}
        depth={(r) => r.depth}
        selected={selected}
        onSelect={(r) => select(r.id)}
        problems={(r) => rowProblems(pc.problems, r.pointer)}
        onCommit={commit}
        onAdd={(after) => add(after, false)}
        onRemove={(row) => {
          // The check runs on the current document first: a refused delete makes no change (and no undo step).
          const probe = structuredClone(process);
          const result = deleteState(probe, row.id);
          if (!result.ok) {
            setError(result.reason);
            return;
          }
          setError(null);
          if (!diagramId) {
            change((p) => void deleteState(p, row.id));
            return;
          }
          // The process diagram's members of the deleted states go in the same batch (a member naming a deleted state
          // would refuse the save).
          void changeWithDiagram(services, {
            process: id,
            diagram: diagramId,
            label: `Delete ${row.name}`,
            removed: subtreeIds(process, row.id),
            apply: (p) => {
              const r = deleteState(p, row.id);
              return r.ok ? null : r.reason;
            },
          }).then((failed) => failed && setError(CHART_LABELS.notDeleted(failed)));
        }}
        onKey={(e, row) => {
          if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === "Enter") {
            e.preventDefault();
            add(row, true);
            return true;
          }
          return false;
        }}
        hint="Enter edits · Ctrl+Enter adds a sibling · Ctrl+Shift+Enter adds a child · Ctrl+Delete deletes"
        toolbar={
          <Button
            size="sm"
            variant="ghost"
            data-testid="states-grid-add-child"
            disabled={!selected}
            onClick={() => {
              const row = rows.find((r) => r.id === selected);
              if (row) add(row, true);
            }}
          >
            <ListTree /> Add child state
          </Button>
        }
      />
      {selected ? <InvokesGrid pc={pc} state={selected} /> : null}
    </div>
  );
}

interface InvokeRow {
  id: string;
  name: string;
  type: string;
  process: string;
  actors: string;
  pointer: string;
}

const invokesOf = (p: ProcessDoc, state: string): InvokeDoc[] | undefined => stateIndex(p).get(state)?.state.invoke;

/** The selected state's invokes (phase-3-design.md 6.2), one keyboard grid; a removal an invoke-done or invoke-error
 * transition still uses is refused. */
function InvokesGrid({ pc, state }: { pc: ProcessContext; state: string }) {
  const { process, change } = pc;
  const [error, setError] = useState<string | null>(null);
  const index = useIndex();
  const go = useGridTargets(pc.id);
  const invoke = (r: InvokeRow) => invokesOf(process, state)?.find((i) => i.id === r.id);
  const processes = indexLookup(index.data)
    .ofKind("process")
    .filter((x) => x.id !== pc.id);
  const entry = stateIndex(process).get(state);
  if (!entry) return null;
  const actorName = new Map(pc.actors.map((a) => [a.id, a.name]));
  const processName = new Map(processes.map((x) => [x.id, x.name]));
  const pointer = (i: number) => `${entry.pointer}/invoke/${i}`;
  const rows: InvokeRow[] = (entry.state.invoke ?? []).map((i, n) => ({
    id: i.id,
    name: i.name,
    type: i.type ?? "",
    process: i.process ? (processName.get(i.process) ?? i.process) : "",
    actors: (i.actors ?? []).map((a) => actorName.get(a) ?? a).join(", "),
    pointer: pointer(n),
  }));
  const columns: GridColumn<InvokeRow>[] = [
    { key: "name", label: "Name", kind: "text", width: "min-w-28", value: (r) => r.name },
    { key: "type", label: "Type", kind: "select", width: "w-28", value: (r) => r.type, options: () => INVOKE_TYPES.map((t) => ({ value: t, label: t })) },
    {
      key: "process",
      label: "Process",
      kind: "select",
      width: "min-w-32",
      value: (r) => r.process,
      editValue: (r) => invokesOf(process, state)?.find((i) => i.id === r.id)?.process ?? "",
      locked: (r) => r.type !== "process",
      definition: (r) => go.element(invoke(r)?.process),
      options: () => [{ value: "", label: "(none)" }, ...processes.map((x) => ({ value: x.id, label: x.name }))],
    },
    {
      key: "actors",
      label: "Actors",
      kind: "multi",
      width: "min-w-32",
      value: (r) => r.actors,
      locked: (r) => r.type !== "human-task",
      definition: (r) => go.element(invoke(r)?.actors?.[0]),
      options: () => pc.actors.map((a) => ({ value: a.id, label: a.name })),
    },
  ];
  const name = entry.state.displayName || entry.state.name;
  return (
    <section className="flex flex-col gap-1" aria-label={`Invokes of ${name}`}>
      <SectionTitle>{`Invokes of ${name}`}</SectionTitle>
      {error ? (
        <p role="alert" className="text-12 text-danger" data-testid="invokes-error">
          {error}
        </p>
      ) : null}
      <Grid
        label={`Invokes of ${name}`}
        testid="invokes-grid"
        noun="invoke"
        rows={rows}
        columns={columns}
        rowName={(r) => r.name}
        problems={(r) => rowProblems(pc.problems, r.pointer)}
        empty={`${name} invokes nothing.`}
        onCommit={(row, key, value) => {
          setError(null);
          change((p) => {
            const invoke = invokesOf(p, state)?.find((i) => i.id === row.id);
            if (!invoke) return;
            const v = String(value);
            if (key === "name") invoke.name = v.trim() || invoke.name;
            else if (key === "type") {
              invoke.type = v as InvokeDoc["type"];
              if (v !== "process") delete invoke.process;
              if (v !== "human-task") delete invoke.actors;
            } else if (key === "process") {
              if (v) invoke.process = v;
              else delete invoke.process;
            } else if (key === "actors") {
              const ids = idsOfNames(v, pc.actors);
              if (ids.length) invoke.actors = ids;
              else delete invoke.actors;
            }
          });
        }}
        onAdd={() => {
          setError(null);
          change((p) => {
            const target = stateIndex(p).get(state)?.state;
            if (!target) return;
            const taken = [...stateIndex(p).values()].flatMap(({ state: x }) => (x.invoke ?? []).map((i) => i.name));
            (target.invoke ??= []).push({ id: newId(), name: uniqueName(taken, "invoke"), type: "service" });
          });
        }}
        onRemove={(row) => {
          const users = (process.transitions ?? []).filter((t) => t.invoke === row.id).length;
          if (users) {
            setError(`${users === 1 ? "A transition still waits" : `${users} transitions still wait`} on ${row.name}; change its trigger first.`);
            return;
          }
          setError(null);
          change((p) => {
            const target = stateIndex(p).get(state)?.state;
            if (!target?.invoke) return;
            target.invoke = target.invoke.filter((i) => i.id !== row.id);
            if (!target.invoke.length) delete target.invoke;
          });
        }}
      />
    </section>
  );
}
