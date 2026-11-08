// The named trigger point's editor (trigger-points-design.md section 11, part 2): the fields of a row of a lifecycle's Trigger points
// grid without states, targets and moves (Does, Type, When it runs, Severity, Where, Only if, What, Outside systems), plus Covers,
// Except and Never skipped by a bulk load, and the types it runs for, read from what the index knows of entity bases and
// stereotypes. Each commit is one draft change and is flushed, as the named right's editor does.
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Wrench } from "lucide-react";
import { useElement, useIndex } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { DeleteButton, References } from "@/inspector/Inspector";
import { deriveQuickFix } from "@/problems/quickFix";
import { SEVERITY_ICON, SEVERITY_TONE } from "@/problems/findings";
import { ChipsEditor, TextField } from "@/inspector/fields";
import { treeText } from "@/model/effects";
import { indexLookup } from "@/model/index";
import { EDITOR_TAB_LABELS, TRIGGER_LABELS as L } from "@/model/labels";
import type { Json, ProcessDoc } from "@/model/process";
import {
  allowsReplace,
  coveredEntities,
  editPoint,
  exceptChoices,
  namedVerbOption,
  namedWhereKeys,
  setNamedExcept,
  setNamedSubjects,
  setText,
  setTriggerWhereOn,
  subjectValue,
  SEVERITY_LABELS,
  REPLACE_VERBS,
  TEAM_CODE,
  TIMING_LABELS,
  TRIGGER_TIMINGS,
  TRIGGER_TYPES,
  TYPE_LABELS,
  WHERE_LABELS,
  fitPointType,
  type NamedSubject,
  type NamedTriggerPointDoc,
  type TriggerType,
  type WhereKey,
} from "@/model/triggerPoints";
import { verbGroups } from "@/model/verbList";
import { useVerbs } from "@/verbs/useVerbs";
import { useObjectFields } from "../actor/useObjectFields";
import { getTriggerPointCovers } from "../process/api";
import { useProblems } from "../process/shared";
import { useStereotypes } from "../process/ProcessControls";
import { SetsDialog, useSetContext } from "../process/SetsEditor";
import { ValueBox } from "../ValueBox";
import { domIdOf, EditorLayout, isRightsFinding, MarkChips, useEditorContext, type EditorContext } from "../EditorFrame";

const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);

export function TriggerPointEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "trigger-point");
  if (!ctx) return <>{fallback}</>;
  return <TriggerPointBody ctx={ctx} draft={draft} />;
}

function TriggerPointBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const dom = domIdOf(ctx.id);
  const point = ctx.json as unknown as NamedTriggerPointDoc;
  const verbs = useVerbs();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const stereotypes = useStereotypes();
  const [refused, setRefused] = useState<string | null>(null);
  const [sets, setSets] = useState(false);
  const subjects = Array.isArray(point.subjects) ? point.subjects : [];
  const pointVerbs = strings(point.verbs);
  const type: TriggerType = point.type === "action" || point.type === "replace" ? point.type : "check";
  // What the pickers and the previews read: what every entity it covers holds under one id (decision 33).
  const rows = lookup.ofKind("entity").map((e) => ({ id: e.id, name: e.displayName || e.name, base: e.base ?? null, stereotypes: e.stereotypes ?? [] }));
  const covered = coveredEntities(point, rows);
  // What it runs for is the engine's list (GET /api/model/trigger-points/{id}/covers), never worked out here.
  const hash = useElement(ctx.id).data?.hash;
  const coversList = useQuery({
    queryKey: ["trigger-point-covers", ctx.id, hash, JSON.stringify(index.data?.map((r) => [r.id, r.base, r.stereotypes, r.package]))],
    queryFn: ({ signal }) => getTriggerPointCovers(ctx.id, signal),
    staleTime: 5_000,
  });
  const runsFor = (coversList.data ?? []).map((id) => ({ id, name: rows.find((e) => e.id === id)?.name ?? "?" }));
  const findings = useProblems(ctx.id, ctx.diagnostics).filter(isRightsFinding);
  const holder = { kind: "process", id: ctx.id, name: point.name, subjects: subjects.map((s) => ({ id: "", ...s })), states: [] } as unknown as ProcessDoc;
  const setContext = useSetContext(holder);
  const objectFields = useObjectFields(strings(point.entities).length ? strings(point.entities) : (runsFor.length ? runsFor : covered).map((e) => e.id));
  const set = (update: (j: NamedTriggerPointDoc) => void) => {
    ctx.edit((j) => void update(j as unknown as NamedTriggerPointDoc));
    ctx.flush();
  };
  const cell = (key: string, value: string | boolean) => set((j) => void editPoint(j as never, key, value));
  const subjectOptions = [
    ...rows.map((e) => ({ value: subjectValue({ entity: e.id }), label: e.name })),
    ...stereotypes.filter((s) => s.forEntities).map((s) => ({ value: subjectValue({ stereotype: s.key }), label: `«${s.name}»` })),
  ];
  const subjectLabel = (s: NamedSubject): string =>
    s.entity !== undefined ? (rows.find((e) => e.id === s.entity)?.name ?? "?") : `«${stereotypes.find((x) => x.key === s.stereotype)?.name ?? s.stereotype}»`;
  // A chip that names a subject the model lacks still shows, so the author can take it away.
  const known = new Set(subjectOptions.map((o) => o.value));
  const subjectChoices = [
    ...subjectOptions,
    ...subjects.filter((s) => !known.has(subjectValue(s))).map((s) => ({ value: subjectValue(s), label: subjectLabel(s) })),
  ];
  const verbOptions = verbGroups("trigger-point", verbs.own, verbs.hidden, undefined)
    .flatMap((g) => g.options.map((o) => ({ value: o.key, label: o.label })))
    .filter((o) => namedVerbOption(o.value) || pointVerbs.includes(o.value))
    // A replace is offered only for the verbs that allow it (decision 17).
    .filter((o) => type !== "replace" || REPLACE_VERBS.has(o.value) || pointVerbs.includes(o.value));
  const placeKeys = namedWhereKeys(pointVerbs);
  const options: Record<WhereKey, { value: string; label: string }[]> = {
    states: [],
    targets: [],
    transitions: [],
    entities: covered.map((e) => ({ value: e.id, label: e.name })),
    attributes: objectFields.fields.filter((f) => f.kind === "attribute").map((f) => ({ value: f.id, label: f.name })),
    ends: objectFields.fields.filter((f) => f.kind === "end").map((f) => ({ value: f.id, label: f.name })),
  };
  const teamCode = type === "check" ? !point.condition : !(Array.isArray(point.sets) && point.sets.length);
  const what =
    type === "check"
      ? treeText(point.condition as Json | undefined, setContext.names)
      : (point.sets ?? []).map((s) => setText(s, { field: setContext.attributeName, expression: (t) => treeText(t, setContext.names) })).join("; ");
  const text = (key: "when" | "condition"): string => treeText(point[key] as Json | undefined, setContext.names);
  const expression = (key: "when" | "condition", label: string, placeholder: string) => {
    const stored = point[key] as Json | undefined;
    return (
      <ValueBox
        id={`${dom}-${key}`}
        label={label}
        initial={text(key)}
        placeholder={placeholder}
        condition
        request={(t) =>
          t === undefined ? (stored ? { element: ctx.id, at: `/${key}` } : null) : t.trim() ? { element: ctx.id, site: "trigger", text: t } : null
        }
        onChange={() => undefined}
        onCommit={(t) => cell(key, t)}
      />
    );
  };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex flex-col gap-2" data-testid="trigger-point-controls">
          <div className="grid grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] items-end gap-2">
            <TextField
              id={`${dom}-name`}
              label={L.columns.name}
              value={String(point.name ?? "")}
              invalid={ctx.diagnostics.some((d) => d.jsonPointer === "/name")}
              onChange={(v) => ctx.edit((j) => void ((j as unknown as NamedTriggerPointDoc).name = v))}
              onBlur={ctx.flush}
            />
            <Field label={L.columns.type} htmlFor={`${dom}-type`} hint={L.tips.type}>
              <Select
                id={`${dom}-type`}
                aria-label={L.columns.type}
                value={type}
                onChange={(e) => {
                  const next = e.target.value as TriggerType;
                  setRefused(next === "replace" && !allowsReplace(pointVerbs) ? L.noReplace : null);
                  if (next !== "replace" || allowsReplace(pointVerbs)) set((j) => void fitPointType(j, next));
                }}
              >
                {/* A replace is offered only for the verbs that allow it (decision 17). */}
                {TRIGGER_TYPES.filter((t) => t !== "replace" || type === "replace" || allowsReplace(pointVerbs)).map((t) => (
                  <option key={t} value={t}>
                    {TYPE_LABELS[t]}
                  </option>
                ))}
              </Select>
            </Field>
            {type === "action" ? (
              <Field label={L.columns.timing} htmlFor={`${dom}-timing`} hint={L.tips.timing}>
                <Select id={`${dom}-timing`} aria-label={L.columns.timing} value={point.timing ?? "before"} onChange={(e) => cell("timing", e.target.value)}>
                  {TRIGGER_TIMINGS.map((t) => (
                    <option key={t} value={t}>
                      {TIMING_LABELS[t]}
                    </option>
                  ))}
                </Select>
              </Field>
            ) : null}
            {type === "check" ? (
              <Field label={L.columns.severity} htmlFor={`${dom}-severity`} hint={L.tips.severity}>
                <Select
                  id={`${dom}-severity`}
                  aria-label={L.columns.severity}
                  value={point.severity === "warning" ? "warning" : "error"}
                  onChange={(e) => cell("severity", e.target.value)}
                >
                  {(Object.keys(SEVERITY_LABELS) as (keyof typeof SEVERITY_LABELS)[]).map((s) => (
                    <option key={s} value={s}>
                      {SEVERITY_LABELS[s]}
                    </option>
                  ))}
                </Select>
              </Field>
            ) : null}
          </div>
          <MarkChips {...ctx} stereotypes={false} />
        </div>
      }
      tabs={[
        {
          value: "general",
          label: "General",
          content: (
            <div className="flex flex-col gap-2" data-testid="trigger-point-general">
              <NamedFindings
                findings={findings}
                element={ctx.json as ModelJson}
                id={ctx.id}
                name={String(point.name ?? "")}
                onFix={(json) =>
                  ctx.edit((j) => {
                    for (const k of Object.keys(j)) delete (j as Record<string, unknown>)[k];
                    Object.assign(j, json);
                  })
                }
                flush={ctx.flush}
              />
              <section
                className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5"
                aria-label={L.named.runsFor}
                data-testid="trigger-point-runs-for"
                title={L.named.runsForNote}
              >
                <SectionTitle>{L.named.runsFor}</SectionTitle>
                {coversList.isPending ? (
                  <span className="text-12 text-secondary">{L.named.runsForLoading}</span>
                ) : coversList.isError ? (
                  <span className="text-12 text-danger">{L.named.runsForFailed}</span>
                ) : runsFor.length ? (
                  <ul className="flex flex-wrap gap-x-3 gap-y-0.5 text-12">
                    {runsFor.map((e) => (
                      <li key={e.id}>{e.name}</li>
                    ))}
                  </ul>
                ) : (
                  <span className="text-12 text-secondary">{L.named.runsForNone}</span>
                )}
              </section>
              <div className="grid grid-cols-[repeat(auto-fill,minmax(14rem,1fr))] gap-2">
                <div data-testid="trigger-point-covers" title={L.named.coversTip}>
                  <ChipsEditor
                    label={L.named.covers}
                    values={subjects.map(subjectValue)}
                    options={subjectChoices}
                    allowFree={false}
                    onChange={(v) => {
                      // The last one cannot go: an empty list would run it everywhere.
                      const ok = v.length > 0;
                      setRefused(ok ? null : L.named.coversEmpty);
                      if (ok) set((j) => void setNamedSubjects(j, v));
                    }}
                  />
                </div>
                <div data-testid="trigger-point-except" title={L.named.exceptTip}>
                  <ChipsEditor
                    label={L.named.except}
                    values={strings(point.exceptEntities)}
                    // An entry nothing covers any more keeps its name, so the author can read it and take it away.
                    options={[
                      ...exceptChoices(point, rows).map((e) => ({ value: e.id, label: e.name })),
                      ...strings(point.exceptEntities)
                        .filter((x) => !exceptChoices(point, rows).some((e) => e.id === x))
                        .map((x) => ({ value: x, label: rows.find((e) => e.id === x)?.name ?? "?" })),
                    ]}
                    allowFree={false}
                    onChange={(v) => set((j) => void setNamedExcept(j, v))}
                  />
                </div>
                <ChipsEditor
                  label={L.columns.does}
                  values={pointVerbs}
                  options={verbOptions}
                  allowFree={false}
                  onChange={(v) => {
                    const refuse = !v.length || (type === "replace" && !allowsReplace(v));
                    setRefused(refuse ? (type === "replace" ? L.noReplace : null) : null);
                    if (!refuse) cell("verbs", v.join(","));
                  }}
                />
                {placeKeys.map((k) => (
                  <ChipsEditor
                    key={k}
                    label={k === "entities" ? L.named.types : WHERE_LABELS[k]}
                    values={strings(point[k as "entities" | "attributes" | "ends"])}
                    options={options[k]}
                    allowFree={false}
                    whenNone={k === "entities" ? L.named.typesNone : L.everywhere}
                    onChange={(v) => set((j) => void setTriggerWhereOn(j as never, { [k]: v }))}
                  />
                ))}
              </div>
              {refused ? (
                <p role="alert" className="text-12 text-danger" data-testid="trigger-point-refused">
                  {refused}
                </p>
              ) : null}
              <div className="grid grid-cols-1 gap-2 lg:grid-cols-2">
                <section className="flex flex-col gap-1" aria-label={L.columns.when}>
                  <SectionTitle>{L.columns.when}</SectionTitle>
                  <p className="text-11 text-secondary">{L.whenHint}</p>
                  {expression("when", L.columns.when, L.whenPlaceholder)}
                </section>
                <section className="flex flex-col gap-1" aria-label={L.columns.what} data-testid="trigger-point-what">
                  <SectionTitle>{L.columns.what}</SectionTitle>
                  {type === "check" ? (
                    <>
                      <p className="text-11 text-secondary">{L.conditionHint}</p>
                      {expression("condition", L.conditionTitle(point.name), L.conditionPlaceholder)}
                      <p className="text-11 text-secondary" data-testid="trigger-point-message-note">
                        {point.description ? L.named.messageNote : `${L.noMessage} ${L.named.messageNote}`}
                      </p>
                    </>
                  ) : (
                    <div className="flex items-center gap-2 text-12">
                      <span className="min-w-0 truncate font-mono" title={teamCode ? L.tips.what : what} data-testid="trigger-point-sets">
                        {teamCode ? <span className="font-sans text-secondary">{TEAM_CODE}</span> : what}
                      </span>
                      <Button size="sm" onClick={() => setSets(true)} data-testid="trigger-point-edit-sets">
                        {L.named.editSets}
                      </Button>
                    </div>
                  )}
                </section>
              </div>
              <div className="flex flex-wrap gap-4">
                <label className="flex items-center gap-2 text-12" title={L.tips.outside}>
                  <input
                    type="checkbox"
                    checked={point.outside === true}
                    // Only a point the team writes can reach outside systems (MQ8101).
                    disabled={!teamCode && point.outside !== true}
                    onChange={(e) => cell("outside", e.target.checked)}
                    data-testid="trigger-point-outside"
                  />
                  {L.columns.outside}
                </label>
                <label className="flex items-center gap-2 text-12" title={L.tips.noBypass}>
                  <input
                    type="checkbox"
                    checked={point.noBypass === true}
                    onChange={(e) => cell("noBypass", e.target.checked)}
                    data-testid="trigger-point-no-bypass"
                  />
                  {L.noBypass}
                </label>
              </div>
              {sets && type !== "check" ? (
                <SetsDialog
                  process={holder}
                  action=""
                  holder={{
                    name: point.name,
                    sets: point.sets ?? [],
                    request: (s) => (s.text === undefined ? { element: s.id } : { element: ctx.id, site: "trigger-set", attribute: s.attribute, text: s.text }),
                  }}
                  context={setContext}
                  onApply={(next) => {
                    set((j) => {
                      if (next.length) j.sets = next;
                      else delete j.sets;
                    });
                    setSets(false);
                  }}
                  onClose={() => setSets(false)}
                />
              ) : null}
            </div>
          ),
        },
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}

/** The named point's findings, each once here (Problems lists them too): the rule, the reason, and the quick fix the lifecycle grid offers. */
function NamedFindings({
  findings,
  element,
  id,
  name,
  onFix,
  flush,
}: {
  findings: readonly import("@/api/types").Diagnostic[];
  element: ModelJson;
  id: string;
  name: string;
  onFix: (json: ModelJson) => void;
  flush: () => void;
}) {
  if (!findings.length) return null;
  return (
    <ul className="flex flex-col gap-0.5 text-12" data-testid="trigger-point-findings">
      {findings.map((d, i) => {
        const Icon = SEVERITY_ICON[d.severity];
        const fix = d.rule === "MQ8107" ? null : deriveQuickFix({ ...d, elementId: id }, null, () => element);
        return (
          <li key={i} className="flex flex-wrap items-center gap-1" data-severity={d.severity} data-testid="trigger-point-finding">
            <Icon className={`size-3.5 shrink-0 ${SEVERITY_TONE[d.severity]}`} aria-label={d.severity} />
            <span className={d.severity === "error" ? "text-danger" : undefined}>
              <span className="font-mono">{d.rule}</span> {d.message}
            </span>
            {fix?.kind === "update" ? (
              <Button
                size="sm"
                variant="ghost"
                title={`Quick fix: ${fix.label}`}
                data-testid={`trigger-point-fix-${d.rule}`}
                onClick={() => {
                  onFix(fix.json);
                  flush();
                }}
              >
                <Wrench /> {fix.label}
              </Button>
            ) : null}
            {d.rule === "MQ8107" ? <DeleteButton id={id} name={name} text="Remove it" /> : null}
          </li>
        );
      })}
    </ul>
  );
}
