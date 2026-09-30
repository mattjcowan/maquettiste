// The Transitions tab (phase-3-design.md 6.2): the transitions in priority order (source path, trigger, event or
// duration, guard, targets, actions, external, gate badge), then the Guards and Actions grids (name, the expression
// as a one-line code field with its MQ9501 marker, description, used by).
import { ShieldCheck, ShieldOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge, SectionTitle } from "@/components/ui/misc";
import {
  addNamed,
  addTransition,
  expressionError,
  idsOfNames,
  namedRows,
  newGate,
  removeNamed,
  setTrigger,
  stateIndex,
  transitionRows,
  TRIGGERS,
  type NamedRow,
  type ProcessDoc,
  type TransitionDoc,
  type TransitionRow,
  type Trigger,
} from "@/model/process";
import { Grid, type GridColumn } from "./Grid";
import { rowProblems, useGridTargets, type ProcessContext } from "./shared";
import { selectProcessNode, useProcessSelection } from "./selection";

const find = (p: ProcessDoc, id: string): TransitionDoc | undefined => p.transitions?.find((t) => t.id === id);

export function TransitionsTab({ pc }: { pc: ProcessContext }) {
  const { process, change, id } = pc;
  const selection = useProcessSelection(id);
  const index = stateIndex(process);
  const states = [...index.values()].map((i) => ({ id: i.state.id, name: i.path }));
  const events = process.events ?? [];
  const guards = process.guards ?? [];
  const actions = (process.actions ?? []).map((a) => ({ id: a.id, name: a.name }));
  const rows = transitionRows(process);
  const go = useGridTargets(id);
  const select = (t: string) => selectProcessNode({ process: id, kind: "transition", id: t });
  const invokesOf = (row: TransitionRow) => {
    const t = find(process, row.id);
    return (t ? (index.get(t.source)?.state.invoke ?? []) : []).map((i) => ({ value: i.id, label: i.name }));
  };

  const columns: GridColumn<TransitionRow>[] = [
    {
      key: "source",
      label: "Source",
      kind: "select",
      width: "min-w-32",
      definition: (r) => go.node("states", find(process, r.id)?.source),
      value: (r) => r.source,
      editValue: (r) => find(process, r.id)?.source ?? "",
      options: () => states.map((s) => ({ value: s.id, label: s.name })),
    },
    { key: "trigger", label: "Trigger", kind: "select", width: "w-24", value: (r) => r.trigger, options: () => TRIGGERS.map((t) => ({ value: t, label: t })) },
    {
      key: "detail",
      label: "Event or duration",
      kind: (r) => (r.trigger === "after" ? "text" : r.trigger === "event" || r.trigger.startsWith("invoke-") ? "select" : "readonly"),
      width: "min-w-28",
      mono: false,
      value: (r) => r.detail,
      editValue: (r) => {
        const t = find(process, r.id);
        return r.trigger === "after" ? (t?.after ?? "") : r.trigger === "event" ? (t?.event ?? "") : (t?.invoke ?? "");
      },
      options: (r) => (r.trigger === "event" ? events.map((e) => ({ value: e.id, label: e.name })) : invokesOf(r)),
      definition: (r) => (r.trigger === "event" ? go.node("events", find(process, r.id)?.event) : null),
    },
    {
      key: "guard",
      label: "Guard",
      kind: "select",
      width: "min-w-24",
      value: (r) => r.guard,
      editValue: (r) => find(process, r.id)?.guard ?? "",
      definition: (r) => go.node("transitions", find(process, r.id)?.guard),
      options: () => [{ value: "", label: "(none)" }, ...guards.map((g) => ({ value: g.id, label: g.name }))],
    },
    {
      key: "targets",
      label: "Targets",
      kind: "multi",
      width: "min-w-32",
      value: (r) => r.targets,
      definition: (r) => go.node("states", find(process, r.id)?.targets?.[0]),
      options: () => states.map((s) => ({ value: s.id, label: s.name })),
    },
    {
      key: "actions",
      label: "Actions",
      kind: "multi",
      width: "min-w-24",
      value: (r) => r.actions,
      definition: (r) => go.node("transitions", find(process, r.id)?.actions?.[0]),
      options: () => actions.map((a) => ({ value: a.id, label: a.name })),
    },
    { key: "external", label: "External", kind: "toggle", width: "w-14", value: (r) => (r.external ? "yes" : ""), on: (r) => r.external },
    {
      key: "gate",
      label: "Gate",
      kind: "readonly",
      width: "w-28",
      value: (r) => r.gate,
      render: (r) =>
        r.gate ? (
          <Badge tone="accent" data-testid="gate-badge">
            <ShieldCheck className="size-3" aria-hidden /> {r.gate}
          </Badge>
        ) : null,
    },
  ];

  const commit = (row: TransitionRow, key: string, value: string | boolean) =>
    change((p) => {
      const t = find(p, row.id);
      if (!t) return;
      const v = String(value);
      if (key === "source" && v) t.source = v;
      else if (key === "trigger") setTrigger(t, v as Trigger, p);
      else if (key === "detail") {
        if (row.trigger === "after") t.after = v.trim();
        else if (row.trigger === "event") t.event = v;
        else t.invoke = v;
      } else if (key === "guard") {
        if (v) t.guard = v;
        else delete t.guard;
      } else if (key === "targets") {
        const ids = idsOfNames(v, states);
        if (ids.length) t.targets = ids;
        else delete t.targets;
      } else if (key === "actions") {
        const ids = idsOfNames(v, actions);
        if (ids.length) t.actions = ids;
        else delete t.actions;
      } else if (key === "external") {
        if (value) t.external = true;
        else delete t.external;
      }
    });

  /** Adds a gate to an event transition (signed by the event's first actor), or removes the gate it has. */
  const toggleGate = (row: TransitionRow) => {
    if (row.trigger !== "event") return;
    change((p) => {
      const t = find(p, row.id);
      if (!t) return;
      if (t.gate) delete t.gate;
      else t.gate = newGate(p, p.events?.find((e) => e.id === t.event)?.actors?.[0] ?? pc.actors[0]?.id);
    });
  };

  return (
    <div className="flex flex-col gap-3">
      <Grid
        label={`Transitions of ${process.name}`}
        testid="transitions-grid"
        noun="transition"
        rows={rows}
        columns={columns}
        rowName={(r) => `${r.source} ${r.detail}`.trim()}
        selected={selection?.kind === "transition" ? selection.id : null}
        onSelect={(r) => select(r.id)}
        problems={(r) => rowProblems(pc.problems, r.pointer)}
        onCommit={commit}
        onAdd={(after) => {
          const created: string[] = [];
          const source = after ? find(process, after.id)?.source : selection?.kind === "state" ? selection.id : undefined;
          change((p) => void created.push(addTransition(p, source, after?.id)));
          if (created[0]) select(created[0]);
        }}
        onRemove={(row) => change((p) => void (p.transitions = (p.transitions ?? []).filter((t) => t.id !== row.id)))}
        onKey={(e, row) => {
          // Ctrl+G (Cmd+G) adds or removes the gate of an event transition, as the row's gate button does.
          if (!(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey || e.key.toLowerCase() !== "g") return false;
          e.preventDefault();
          toggleGate(row);
          return true;
        }}
        hint="Enter edits · Tab moves · Ctrl+Enter adds · Ctrl+Delete removes · Ctrl+G adds or removes a gate"
        actions={(row) =>
          row.trigger !== "event" ? null : (
            <Button
              variant="ghost"
              size="icon-row"
              tabIndex={-1}
              label={row.gate ? `Remove the gate of ${row.source}` : `Add a gate to ${row.source} ${row.detail}`}
              shortcut="Ctrl+G"
              onClick={() => toggleGate(row)}
            >
              {row.gate ? <ShieldOff /> : <ShieldCheck />}
            </Button>
          )
        }
      />
      <ExpressionGrid pc={pc} field="guards" />
      <ExpressionGrid pc={pc} field="actions" />
    </div>
  );
}

/** The Guards or the Actions grid. */
function ExpressionGrid({ pc, field }: { pc: ProcessContext; field: "guards" | "actions" }) {
  const { process, change } = pc;
  const rows = namedRows(process, field);
  const noun = field === "guards" ? "guard" : "action";
  const columns: GridColumn<NamedRow>[] = [
    { key: "name", label: "Name", kind: "text", width: "min-w-28", value: (r) => r.name },
    {
      key: "expression",
      label: "Expression",
      kind: "text",
      width: "min-w-64",
      mono: true,
      value: (r) => r.expression,
      marker: (r) => {
        const local = expressionError(r.expression);
        if (local) return { rule: "MQ9501", message: local };
        const found = pc.problems.find((d) => d.rule === "MQ9501" && d.jsonPointer === `${r.pointer}/expression`);
        return found ? { rule: found.rule, message: found.message } : null;
      },
    },
    { key: "description", label: "Description", kind: "text", width: "min-w-32", value: (r) => r.description },
    { key: "usedBy", label: "Used by", kind: "readonly", width: "min-w-32", value: (r) => r.usedBy },
  ];
  return (
    <section className="flex flex-col gap-1" aria-label={field === "guards" ? "Guards" : "Actions"}>
      <SectionTitle>{field === "guards" ? "Guards" : "Actions"}</SectionTitle>
      <Grid
        label={`${field === "guards" ? "Guards" : "Actions"} of ${process.name}`}
        testid={`${field}-grid`}
        noun={noun}
        rows={rows}
        columns={columns}
        rowName={(r) => r.name}
        problems={(r) => rowProblems(pc.problems, r.pointer).filter((d) => d.rule !== "MQ9501")}
        onCommit={(row, key, value) =>
          change((p) => {
            const x = (p[field] ?? []).find((g) => g.id === row.id);
            if (!x) return;
            const v = String(value);
            if (key === "name") x.name = v.trim() || x.name;
            else if (key === "expression") {
              if (v.trim()) x.expression = v;
              else delete x.expression;
            } else if (key === "description") {
              if (v.trim()) x.description = v;
              else delete x.description;
            }
          })
        }
        onAdd={(after) => change((p) => void addNamed(p, field, after?.id))}
        onRemove={(row) => change((p) => removeNamed(p, field, row.id))}
      />
    </section>
  );
}
