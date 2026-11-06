// The Rights tab (rights-design.md section 10), lifecycles only: "When nothing is said" (the lifecycle's default) above the
// rights grid, one row per right: Name, Allows or denies, Who, Except, Does, In states, Links (only when a right uses it),
// When and Why. A use of a named right shows the right's fields read-only, with only In states editable, and a button to open
// the right. The grid follows the other process grids' keyboard model; each commit is one draft change.
import { useMemo, useState } from "react";
import { ExternalLink } from "lucide-react";
import { useQuery } from "@tanstack/react-query";
import { useElements } from "@/api/queries";
import { previewExpression } from "@/api/expressionPreview";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { EFFECT_LABELS } from "@/model/effectLabels";
import { RIGHT_LABELS as L } from "@/model/labels";
import { treeText } from "@/model/effects";
import { subjectsOf, type Json } from "@/model/process";
import {
  addRight,
  addRightUse,
  editRight,
  hasEnds,
  removeRight,
  rightRows,
  rightStateOptions,
  setRightsDefault,
  type RightAccess,
  type RightRow,
} from "@/model/rights";
import { describeVerb, verbGroups } from "@/model/verbList";
import { useVerbs } from "@/verbs/useVerbs";
import { useObjectFields } from "../actor/useObjectFields";
import { Grid, type GridColumn } from "./Grid";
import { useSetContext } from "./SetsEditor";
import { rowProblems, useGridTargets, type ProcessContext } from "./shared";
import { useNamedRights } from "./useNamedRights";
import { WhoCanDo } from "./WhoCanDo";

export function RightsTab({ pc }: { pc: ProcessContext }) {
  const { process, change, actorNames } = pc;
  const go = useGridTargets(pc.id);
  const verbs = useVerbs();
  const setContext = useSetContext(process);
  const named = useNamedRights();
  const used = (process.rights ?? []).flatMap((r) => (r.right ? [r.right] : []));
  const docs = useElements(used).byId;
  const namedDocs = useMemo(() => new Map([...docs].map(([id, d]) => [id, d.json as unknown as Json])), [docs]);
  const entities = subjectsOf(process).flatMap((s) => s.entity ?? []);
  const objectFields = useObjectFields(entities);
  const states = rightStateOptions(process);
  const stateName = new Map(states.map((s) => [s.value, s.label]));
  const rows = rightRows(process, namedDocs, {
    actor: (id) => actorNames.get(id) ?? "?",
    state: (id) => stateName.get(id) ?? "?",
    end: (id) => objectFields.nameOf(id),
    verb: (key) => describeVerb(key, verbs.own)?.label ?? key,
    condition: (tree) => treeText(tree, setContext.names),
  });
  const [picked, setPicked] = useState<string | null>(null);
  const [view, setView] = useState<"rights" | "who">("rights");
  const current = rows.find((r) => r.id === picked) ?? rows[0] ?? null;
  const actorOptions = pc.actors.map((a) => ({ id: a.id, name: a.name }));
  const verbOptions = verbGroups("right", verbs.own, verbs.hidden).flatMap((g) => g.options.map((o) => ({ value: o.key, label: o.label, group: g.label })));
  const endOptions = objectFields.fields.filter((f) => f.kind === "end").map((f) => ({ value: f.id, label: f.name }));
  const names = (ids: string[]) => ids.map((a) => actorNames.get(a) ?? "").join(", ");
  const columns: GridColumn<RightRow>[] = [
    { key: "name", label: L.columns.name, kind: "text", width: "min-w-32", sticky: true, value: (r) => r.name, locked: (r) => r.use },
    {
      key: "access",
      label: L.columns.access,
      tooltip: L.tips.access,
      kind: "select",
      width: "min-w-28",
      value: (r) => (r.access === "allow" ? L.allow : L.deny),
      editValue: (r) => r.access,
      options: () => [
        { value: "allow", label: L.allow },
        { value: "deny", label: L.deny },
      ],
      locked: (r) => r.use,
    },
    {
      key: "who",
      label: L.columns.who,
      tooltip: L.tips.who,
      kind: "multi",
      width: "min-w-36",
      value: (r) => r.who,
      editValue: (r) => names(r.actorIds),
      definition: (r) => go.element(r.actorIds[0]),
      options: () => actorOptions.map((a) => ({ value: a.id, label: a.name })),
      locked: (r) => r.use,
    },
    {
      key: "except",
      label: L.columns.except,
      tooltip: L.tips.except,
      kind: "multi",
      width: "min-w-32",
      value: (r) => r.except,
      editValue: (r) => names(r.exceptIds),
      definition: (r) => go.element(r.exceptIds[0]),
      options: () => actorOptions.map((a) => ({ value: a.id, label: a.name })),
      locked: (r) => r.use,
    },
    {
      key: "verbs",
      label: L.columns.does,
      tooltip: L.tips.does,
      kind: "pick",
      width: "min-w-40",
      value: (r) => r.verbs,
      editValue: (r) => r.verbKeys.join(","),
      options: () => verbOptions,
      locked: (r) => r.use,
    },
    {
      key: "states",
      label: L.columns.states,
      tooltip: L.tips.states,
      kind: "pick",
      width: "min-w-40",
      value: (r) => r.states,
      editValue: (r) => r.stateIds.join(","),
      definition: (r) => go.node("states", r.stateIds[0]),
      options: () => states,
    },
    ...(hasEnds(rows)
      ? [
          {
            key: "ends",
            label: L.columns.links,
            tooltip: L.tips.links,
            kind: "pick",
            width: "min-w-32",
            value: (r: RightRow) => r.ends,
            editValue: (r: RightRow) => r.endIds.join(","),
            options: () => endOptions,
            locked: (r: RightRow) => r.use,
          } satisfies GridColumn<RightRow>,
        ]
      : []),
    { key: "when", label: L.columns.when, tooltip: L.tips.when, kind: "text", width: "min-w-48", mono: true, value: (r) => r.when, locked: (r) => r.use },
    { key: "why", label: L.columns.why, tooltip: L.tips.why, kind: "text", width: "min-w-48", value: (r) => r.why, locked: (r) => r.use },
  ];
  const free = named.filter((n) => !used.includes(n.id));
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-end gap-2">
        <Field label={L.defaultLabel} htmlFor="rights-default" hint={L.defaultTip}>
          <Select
            id="rights-default"
            className="h-6 w-32"
            data-testid="rights-default"
            value={process.rightsDefault === "deny" ? "deny" : "allow"}
            onChange={(e) => change((p) => setRightsDefault(p, e.target.value as RightAccess))}
          >
            <option value="allow">{L.allowed}</option>
            <option value="deny">{L.denied}</option>
          </Select>
        </Field>
        <div role="group" aria-label={L.viewsLabel} className="flex gap-1" data-testid="rights-views">
          {(["rights", "who"] as const).map((v) => (
            <Button
              key={v}
              size="sm"
              variant={view === v ? "primary" : "secondary"}
              aria-pressed={view === v}
              onClick={() => setView(v)}
              data-testid={`rights-view-${v}`}
            >
              {v === "rights" ? L.viewRights : L.viewWho}
            </Button>
          ))}
        </div>
      </div>
      {view === "who" ? <WhoCanDo pc={pc} named={namedDocs} /> : null}
      {view === "rights" ? (
        <Grid
          label={`Rights of ${process.name}`}
          testid="rights-grid"
          noun="right"
          rows={rows}
          columns={columns}
          rowName={(r) => r.name}
          selected={current?.id ?? null}
          onSelect={(r) => setPicked(r.id)}
          problems={(r) => rowProblems(pc.problems, r.pointer)}
          empty={L.empty}
          onCommit={(r, key, value) => change((p) => void editRight(p, r.id, key, value, { actors: actorOptions }))}
          onAdd={(after) => {
            const created: string[] = [];
            change((p) => void created.push(addRight(p, after?.id)));
            if (created[0]) setPicked(created[0]);
          }}
          onRemove={(r) => change((p) => removeRight(p, r.id))}
          actions={(r) =>
            r.named ? (
              <Button variant="ghost" size="icon-row" label={L.open} onClick={go.element(r.named)?.go} data-testid="open-named-right">
                <ExternalLink />
              </Button>
            ) : null
          }
          toolbar={
            <Select
              aria-label={L.useNamed}
              className="h-6 w-44"
              data-testid="use-named-right"
              value=""
              disabled={!free.length}
              title={free.length ? undefined : L.noNamed}
              onChange={(e) => {
                const id = e.target.value;
                if (!id) return;
                const created: string[] = [];
                change((p) => void created.push(addRightUse(p, id)));
                if (created[0]) setPicked(created[0]);
              }}
            >
              <option value="">{L.useNamed}</option>
              {free.map((n) => (
                <option key={n.id} value={n.id}>
                  {n.name}
                </option>
              ))}
            </Select>
          }
        />
      ) : null}
      {current && view === "rights" ? <ConditionLine row={current} lifecycle={pc.id} /> : null}
    </div>
  );
}

/** The selected right's condition, printed with its type: what the engine makes of the text (its findings show on the row). */
function ConditionLine({ row, lifecycle }: { row: RightRow; lifecycle: string }) {
  const request = row.when ? (row.use ? { element: row.named!, lifecycle } : { element: row.id }) : null;
  const preview = useQuery({
    queryKey: ["expression-preview", request],
    queryFn: ({ signal }) => previewExpression(request!, signal),
    enabled: !!request,
    staleTime: 5_000,
  });
  const detail = preview.data?.preview;
  if (!detail) return null;
  return (
    <p className="truncate text-11 text-secondary" title={detail.text} data-testid="right-condition-preview">
      {L.conditionOf(row.name)}: {detail.text} · {EFFECT_LABELS.previewType(detail.type, detail.nullable)}
    </p>
  );
}
