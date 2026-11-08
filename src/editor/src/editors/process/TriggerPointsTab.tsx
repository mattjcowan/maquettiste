// The Trigger points tab (trigger-points-design.md section 11), lifecycles only: one row per trigger point in run order, reordered by
// dragging: Name, Does, Type, When it runs (actions), Severity (checks), Where, Only if, What, Outside systems and Why. Where, Only if
// and What open a dialog of their own; the rest are cells, on the other process grids' keyboard model. Each commit is one draft change.
import { useState } from "react";
import { useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/cn";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { ChipsEditor } from "@/inspector/fields";
import { TRIGGER_LABELS as L } from "@/model/labels";
import { indexLookup } from "@/model/index";
import { treeText } from "@/model/effects";
import { subjectsOf, transitionLabel, type Json, type ProcessDoc, type TriggerPointDoc } from "@/model/process";
import { rightStateOptions } from "@/model/rights";
import {
  addTriggerPoint,
  allowsReplace,
  editTriggerPoint,
  moveTriggerPoint,
  removeTriggerPoint,
  setTriggerSets,
  setTriggerWhere,
  triggerPointRows,
  triggerPointsOf,
  whereKeysFor,
  EVERYWHERE,
  SEVERITY_LABELS,
  TEAM_CODE,
  TIMING_LABELS,
  TRIGGER_TIMINGS,
  TRIGGER_TYPES,
  TYPE_LABELS,
  WHERE_LABELS,
  type TriggerPointRow,
  type WhereKey,
} from "@/model/triggerPoints";
import { describeVerb, verbGroups } from "@/model/verbList";
import { useVerbs } from "@/verbs/useVerbs";
import { useObjectFields } from "../actor/useObjectFields";
import { fieldOptionLabel } from "@/model/objectFields";
import { ValueBox } from "../ValueBox";
import { Grid, type GridColumn } from "./Grid";
import { SetsDialog, useSetContext, type SetContext } from "./SetsEditor";
import { rowProblems, type ProcessContext } from "./shared";
import { WhatHappensWhen } from "./WhatHappensWhen";

type Open = { kind: "where" | "when" | "what"; id: string };

export function TriggerPointsTab({ pc }: { pc: ProcessContext }) {
  const { process, change } = pc;
  const verbs = useVerbs();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const setContext = useSetContext(process);
  const subjects = subjectsOf(process).flatMap((s) => s.entity ?? []);
  const objectFields = useObjectFields(subjects);
  const states = rightStateOptions(process);
  const stateName = new Map(states.map((s) => [s.value, s.label]));
  const transitionName = new Map((process.transitions ?? []).map((t) => [t.id, transitionLabel(process, t)]));
  const rows = triggerPointRows(process, {
    state: (id) => stateName.get(id) ?? "?",
    transition: (id) => transitionName.get(id) ?? "?",
    entity: (id) => lookup.byId.get(id)?.name ?? "?",
    field: (id) => setContext.names.name(id) ?? objectFields.nameOf(id),
    verb: (key) => describeVerb(key, verbs.own)?.label ?? key,
    expression: (tree) => treeText(tree, setContext.names),
  });
  // The two sides of the tab: the grid, and What happens when (the answer of get_trigger_sequence).
  const [view, setView] = useState<"points" | "happens">("points");
  const [picked, setPicked] = useState<string | null>(null);
  // An edit the grid refused (a replace beside a verb that does not allow it), said at the grid; the cell keeps what it had.
  const [refused, setRefused] = useState<string | null>(null);
  const [dialog, setDialog] = useState<Open | null>(null);
  const current = rows.find((r) => r.id === picked) ?? rows[0] ?? null;
  const verbOptions = verbGroups("trigger-point", verbs.own, verbs.hidden).flatMap((g) =>
    g.options.map((o) => ({ value: o.key, label: o.label, group: g.label })),
  );
  const open = (kind: Open["kind"]) => (row: TriggerPointRow) => setDialog({ kind, id: row.id });
  const columns: GridColumn<TriggerPointRow>[] = [
    {
      key: "name",
      label: L.columns.name,
      tooltip: L.tips.name,
      kind: "text",
      width: "w-28",
      clip: "max-w-28",
      sticky: true,
      value: (r) => r.name,
      cellTitle: (r) => r.name,
    },
    {
      key: "verbs",
      label: L.columns.does,
      tooltip: L.tips.does,
      kind: "pick",
      width: "w-28",
      clip: "max-w-28",
      value: (r) => r.verbs,
      editValue: (r) => r.verbKeys.join(","),
      options: () => verbOptions,
    },
    {
      key: "type",
      label: L.columns.type,
      tooltip: L.tips.type,
      kind: "select",
      width: "w-16",
      value: (r) => r.typeLabel,
      editValue: (r) => r.type,
      // A replace is offered only for the verbs that allow it (decision 17); the type a row has stays in the list.
      options: (r) =>
        TRIGGER_TYPES.filter((t) => t !== "replace" || r.type === "replace" || allowsReplace(r.verbKeys)).map((t) => ({ value: t, label: TYPE_LABELS[t] })),
    },
    {
      key: "timing",
      label: L.columns.timing,
      tooltip: L.tips.timing,
      kind: "select",
      width: "w-24",
      clip: "max-w-24",
      value: (r) => r.timing,
      editValue: (r) => r.timingKey ?? "before",
      options: () => TRIGGER_TIMINGS.map((t) => ({ value: t, label: TIMING_LABELS[t] })),
      locked: (r) => r.type !== "action",
    },
    {
      key: "severity",
      label: L.columns.severity,
      tooltip: L.tips.severity,
      kind: "select",
      width: "w-[4.5rem]",
      value: (r) => r.severity,
      editValue: (r) => r.severityKey ?? "error",
      options: () => (Object.keys(SEVERITY_LABELS) as (keyof typeof SEVERITY_LABELS)[]).map((s) => ({ value: s, label: SEVERITY_LABELS[s] })),
      locked: (r) => r.type !== "check",
    },
    {
      key: "where",
      label: L.columns.where,
      tooltip: L.tips.where,
      kind: "popover",
      width: "w-28",
      clip: "max-w-28",
      value: (r) => r.where || EVERYWHERE,
      onOpen: open("where"),
      render: (r) =>
        r.whereParts.length ? (
          <span className="flex min-w-0 items-center gap-1" data-testid="where-chips">
            {r.whereParts.map((p) => (
              <span
                key={p.key}
                className="shrink-0 truncate rounded-control border border-default bg-app px-1 text-11"
                title={`${p.label}: ${p.names}`}
                data-where={p.key}
              >
                {p.label}: {p.names}
              </span>
            ))}
          </span>
        ) : (
          <span className="text-secondary">{EVERYWHERE}</span>
        ),
    },
    {
      key: "when",
      label: L.columns.when,
      tooltip: L.tips.when,
      kind: "popover",
      width: "w-24",
      clip: "max-w-24",
      mono: true,
      value: (r) => r.when,
      onOpen: open("when"),
    },
    {
      key: "what",
      label: L.columns.what,
      tooltip: L.tips.what,
      kind: "popover",
      width: "w-28",
      clip: "max-w-28",
      mono: true,
      value: (r) => r.what,
      cellTitle: (r) => (r.teamCode ? L.tips.what : r.what),
      onOpen: open("what"),
      render: (r) => (r.teamCode ? <span className="font-sans text-secondary">{TEAM_CODE}</span> : <span className="truncate">{r.what}</span>),
    },
    {
      key: "outside",
      label: L.columns.outside,
      tooltip: L.tips.outside,
      kind: "toggle",
      width: "w-44",
      toggleLabel: L.outsideShort,
      value: (r) => (r.outside ? "yes" : ""),
      on: (r) => r.outside,
      // Only a point the team writes can reach outside systems (MQ8101).
      locked: (r) => !r.teamCode && !r.outside,
      // The second box: never skipped by a bulk load (decision 20). It sits in this cell so the grid does not widen.
      after: (r) => (
        <>
          <button
            type="button"
            role="checkbox"
            aria-checked={r.noBypass}
            aria-label={`${L.noBypass}: ${r.name}`}
            title={L.noBypass}
            onClick={() => change((p) => void editTriggerPoint(p, r.id, "noBypass", !r.noBypass))}
            className={cn("ml-1 inline-block size-3 shrink-0 rounded-[3px] border border-input", r.noBypass && "border-accent bg-accent")}
            data-testid="trigger-no-bypass"
          />
          <span className="text-11 text-secondary">{L.noBypassShort}</span>
        </>
      ),
    },
    { key: "why", label: L.columns.why, tooltip: L.tips.why, kind: "text", width: "min-w-28", clip: "max-w-36", value: (r) => r.why },
  ];
  return (
    <div className="flex flex-col gap-3">
      <div role="group" aria-label={L.viewsLabel} className="flex gap-1" data-testid="trigger-views">
        {(["points", "happens"] as const).map((v) => (
          <Button
            key={v}
            size="sm"
            variant={view === v ? "primary" : "secondary"}
            aria-pressed={view === v}
            onClick={() => setView(v)}
            data-testid={`trigger-view-${v}`}
          >
            {v === "points" ? L.viewPoints : L.viewHappens}
          </Button>
        ))}
      </div>
      {view === "happens" ? <WhatHappensWhen pc={pc} /> : null}
      {view === "points" && refused ? (
        <p role="alert" className="text-12 text-danger" data-testid="trigger-refused">
          {refused}
        </p>
      ) : null}
      {view === "points" ? (
        <Grid
          label={`Trigger points of ${process.name}`}
          testid="trigger-grid"
          noun="trigger point"
          rows={rows}
          columns={columns}
          rowName={(r) => r.name}
          selected={current?.id ?? null}
          onSelect={(r) => setPicked(r.id)}
          problems={(r) => rowProblems(pc.problems, r.pointer)}
          empty={L.empty}
          hint={`${L.order} Enter edits · Ctrl+Enter adds · Ctrl+Delete removes`}
          onCommit={(r, key, value) => {
            const ids = String(value).split(",").filter(Boolean);
            const refuse =
              (key === "type" && value === "replace" && !allowsReplace(r.verbKeys)) ||
              (key === "verbs" && r.type === "replace" && ids.length > 0 && !allowsReplace(ids));
            setRefused(refuse ? L.noReplace : null);
            if (!refuse) change((p) => void editTriggerPoint(p, r.id, key, value));
          }}
          onReorder={(r, to) => change((p) => void moveTriggerPoint(p, r.id, to))}
          addLabel={L.add}
          onAdd={(after) => {
            const created: string[] = [];
            change((p) => void created.push(addTriggerPoint(p, {}, after?.id)));
            if (created[0]) setPicked(created[0]);
          }}
          onRemove={(r) => change((p) => removeTriggerPoint(p, r.id))}
        />
      ) : null}
      {view === "points" && current && !current.why && current.type === "check" ? <p className="text-11 text-secondary">{L.noMessage}</p> : null}
      {view === "points" && dialog ? (
        <TriggerDialog
          dialog={dialog}
          process={process}
          lifecycle={pc.id}
          context={setContext}
          states={states}
          transitions={[...transitionName].map(([value, label]) => ({ value, label }))}
          entities={subjects.map((id) => ({ value: id, label: lookup.byId.get(id)?.name ?? "?" }))}
          change={change}
          onClose={() => setDialog(null)}
        />
      ) : null}
    </div>
  );
}

/** The dialog behind Where, Only if and What of one trigger point. */
function TriggerDialog({
  dialog,
  process,
  lifecycle,
  context,
  states,
  transitions,
  entities,
  change,
  onClose,
}: {
  dialog: Open;
  process: ProcessDoc;
  lifecycle: string;
  context: SetContext;
  states: { value: string; label: string }[];
  transitions: { value: string; label: string }[];
  entities: { value: string; label: string }[];
  change: (update: (process: ProcessDoc) => void) => void;
  onClose: () => void;
}) {
  const point = triggerPointsOf(process).find((t) => t.id === dialog.id);
  if (!point) return null;
  if (dialog.kind === "where")
    return (
      <WhereDialog
        point={point}
        states={states}
        transitions={transitions}
        entities={entities}
        subjects={entities.map((e) => e.value)}
        change={change}
        onClose={onClose}
      />
    );
  if (dialog.kind === "what" && point.type !== "check")
    return (
      <SetsDialog
        process={process}
        action=""
        holder={{
          name: point.name,
          sets: point.sets ?? [],
          request: (set) =>
            set.text === undefined ? { element: set.id } : { element: lifecycle, site: "trigger-set", attribute: set.attribute, text: set.text },
        }}
        context={context}
        onApply={(sets) => {
          change((p) => void setTriggerSets(p, point.id, sets));
          onClose();
        }}
        onClose={onClose}
      />
    );
  const key = dialog.kind === "when" ? "when" : "condition";
  const at = triggerPointsOf(process).findIndex((t) => t.id === point.id);
  const stored = (point[key] as Json | undefined) ?? undefined;
  return (
    <ExpressionDialog
      title={key === "when" ? L.whenTitle(point.name) : L.conditionTitle(point.name)}
      hint={key === "when" ? L.whenHint : L.conditionHint}
      placeholder={key === "when" ? L.whenPlaceholder : L.conditionPlaceholder}
      initial={treeText(stored, context.names)}
      request={(text) =>
        text === undefined
          ? stored
            ? { element: lifecycle, at: `/triggerPoints/${at}/${key}` }
            : null
          : text.trim()
            ? { element: lifecycle, site: "trigger", text }
            : null
      }
      onApply={(text) => {
        change((p) => void editTriggerPoint(p, point.id, key, text));
        onClose();
      }}
      onClose={onClose}
    />
  );
}

/** Only if and a check's condition: one expression in the value box, with its preview. Apply writes it; empty removes it. */
export function ExpressionDialog({
  title,
  hint,
  placeholder,
  initial,
  request,
  onApply,
  onClose,
  testid = "trigger-expression",
}: {
  title: string;
  hint: string;
  placeholder: string;
  initial: string;
  request: Parameters<typeof ValueBox>[0]["request"];
  /** The test id of the dialog; its Apply button adds `-apply`. */
  testid?: string;
  onApply: (text: string) => void;
  onClose: () => void;
}) {
  const [text, setText] = useState(initial);
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={title} wide>
        <form
          className="flex flex-col gap-2"
          data-testid={`${testid}-dialog`}
          onSubmit={(e) => {
            e.preventDefault();
            // Text left as it was keeps the stored tree.
            if (text === initial) onClose();
            else onApply(text);
          }}
        >
          <p className="text-12 text-secondary">{hint}</p>
          <ValueBox id={testid} label={title} initial={initial} onChange={setText} request={request} placeholder={placeholder} condition />
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" data-testid={`${testid}-apply`}>
              Apply
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Where: every narrowing list of one trigger point, in one popover; only the lists its verbs leave room for. Apply writes them as one change. */
function WhereDialog({
  point,
  states,
  transitions,
  entities,
  subjects,
  change,
  onClose,
}: {
  point: TriggerPointDoc;
  states: { value: string; label: string }[];
  transitions: { value: string; label: string }[];
  entities: { value: string; label: string }[];
  subjects: string[];
  change: (update: (process: ProcessDoc) => void) => void;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState<Record<WhereKey, string[]>>({
    states: point.states ?? [],
    targets: point.targets ?? [],
    transitions: point.transitions ?? [],
    entities: point.entities ?? [],
    attributes: point.attributes ?? [],
    ends: point.ends ?? [],
  });
  // With types picked, attributes and links are those every picked type holds (decision 12); else those every subject holds.
  const fields = useObjectFields(draft.entities.length ? draft.entities : subjects);
  const options: Record<WhereKey, { value: string; label: string }[]> = {
    states,
    targets: states,
    transitions,
    entities,
    attributes: fields.fields.filter((f) => f.kind === "attribute").map((f) => ({ value: f.id, label: f.name })),
    ends: fields.fields.filter((f) => f.kind === "end").map((f) => ({ value: f.id, label: fieldOptionLabel(f) })),
  };
  const keys = whereKeysFor(point.verbs ?? []);
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={L.whereTitle(point.name)} description={L.whereHint}>
        <form
          className="flex flex-col gap-2"
          data-testid="trigger-where-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            // Only the lists the verbs leave room for are written; the others are left as they were.
            change((p) => void setTriggerWhere(p, point.id, Object.fromEntries(keys.map((k) => [k, draft[k]]))));
            onClose();
          }}
        >
          {!point.verbs?.length ? <p className="text-12 text-secondary">{L.whereNone}</p> : null}
          {keys.map((k) => (
            <ChipsEditor
              key={k}
              label={WHERE_LABELS[k]}
              values={draft[k]}
              options={options[k]}
              allowFree={false}
              whenNone={L.everywhere}
              onChange={(values) => setDraft((d) => ({ ...d, [k]: values }))}
            />
          ))}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" data-testid="trigger-where-apply">
              Apply
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
