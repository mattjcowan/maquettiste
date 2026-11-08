// The scenario editor (phase-3-design.md 2.4 and 6.2): the header (process, outcome), the steps grid (input, event,
// actor, signer, meaning, reason, payload, assumptions, expected states and context) and, after Replay, each step's
// status (passed, failed, not run) from the verify result.
import { useState } from "react";
import { Play, Plus } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { useElement, useElements, useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { Badge } from "@/components/ui/misc";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { useScenarioStatuses } from "@/explorer/processApi";
import { TextField } from "@/inspector/fields";
import { newId } from "@/lib/ids";
import { parseValuesText } from "@/model/effects";
import { useObjectFields } from "../actor/useObjectFields";
import { describeVerb, verbGroups } from "@/model/verbList";
import { useVerbs } from "@/verbs/useVerbs";
import { indexLookup } from "@/model/index";
import {
  idsOfNames,
  parseMapText,
  stateIndex,
  stepNames,
  stepRows,
  stepStatuses,
  STEP_INPUTS,
  subjectsOf,
  type ProcessDoc,
  type ScenarioDoc,
  type StepDoc,
  type StepRow,
  type StepStatus,
} from "@/model/process";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { Grid, type GridColumn, type GridOption } from "../process/Grid";
import { refreshScenario, verifyScenario } from "../process/api";
import { failureSentence } from "./replayFailure";
import { useActors, useProblems, rowProblems } from "../process/shared";
import { ScenarioStatusBadge } from "../process/ScenariosTab";
import { ObjectsGrid, ParticipantsGrid } from "./ParticipantsGrid";
import { PARTICIPANT_LABELS as PL, RIGHT_LABELS as RL, TRIGGER_LABELS as TL } from "@/model/labels";
import { EFFECT_LABELS as EL } from "@/model/effectLabels";
import { CHECK_WORD_LABELS, isTeamCheck, parseAssume, reachableChecks } from "@/model/triggerPoints";

type Rec = Record<string, unknown>;

export function ScenarioEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "scenario");
  if (!ctx) return <>{fallback}</>;
  return <ScenarioBody ctx={ctx} draft={draft} />;
}

const STATUS_TONE: Record<StepStatus, "success" | "danger" | "neutral"> = { passed: "success", failed: "danger", "not-run": "neutral" };

/** The scenario's status and each step's, from the last verify (Replay) of it. */
export function useScenarioReplay(scenario: ScenarioDoc) {
  const statuses = useScenarioStatuses();
  const st = statuses.get(scenario.id);
  const steps = stepStatuses(scenario.steps.length, st ? (st.passed ? null : st.step - 1) : undefined);
  return {
    status: !st ? ("not-run" as const) : st.passed ? ("passed" as const) : ("failed" as const),
    failedAt: st && !st.passed ? st.step : null,
    warnings: st?.passed ? (st.warnings ?? []) : [],
    steps,
  };
}

export function ScenarioHeader({ ctx }: { ctx: EditorContext }) {
  const rec = ctx.json as Rec;
  const dom = domIdOf(ctx.id);
  const lookup = indexLookup(useIndex().data);
  const nav = useEditorNavigation();
  const process = lookup.byId.get(String(rec.process ?? ""));
  return (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] items-end gap-2">
      <TextField
        id={`${dom}-name`}
        label="Name"
        value={String(rec.name ?? "")}
        invalid={ctx.diagnostics.some((d) => d.jsonPointer === "/name")}
        onChange={(v) => ctx.edit((j) => void ((j as Rec).name = v))}
        onBlur={ctx.flush}
      />
      <Field label="Process">
        <Button
          size="sm"
          variant="ghost"
          className="justify-start"
          disabled={!process}
          onClick={() => process && nav.openEditor(process, true)}
          data-testid="scenario-process"
        >
          {process ? process.displayName || process.name : "?"}
        </Button>
      </Field>
      <Field label="Outcome" htmlFor={`${dom}-outcome`}>
        <Select
          id={`${dom}-outcome`}
          value={rec.outcome === "final" ? "final" : "active"}
          onChange={(e) => {
            ctx.edit((j) => {
              if (e.target.value === "final") (j as Rec).outcome = "final";
              else delete (j as Rec).outcome;
            });
            ctx.flush();
          }}
        >
          <option value="active">active (still running)</option>
          <option value="final">final (finished)</option>
        </Select>
      </Field>
    </div>
  );
}

function ScenarioBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const scenario = ctx.json as unknown as ScenarioDoc;
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<ScenarioHeader ctx={ctx} />}
      tabs={[
        { value: "steps", label: "Steps", content: <StepsGrid ctx={ctx} scenario={scenario} /> },
        { value: "participants", label: PL.tab, content: <ParticipantsGrid ctx={ctx} scenario={scenario} /> },
        { value: "objects", label: PL.objectsTab, content: <ObjectsGrid ctx={ctx} scenario={scenario} /> },
        codeGeneration,
      ]}
    />
  );
}

function StepsGrid({ ctx, scenario }: { ctx: EditorContext; scenario: ScenarioDoc }) {
  const { store } = useServices();
  const qc = useQueryClient();
  const processDoc = useElement(scenario.process).data?.json as unknown as ProcessDoc | undefined;
  const { actors, actorNames } = useActors();
  const problems = useProblems(ctx.id, ctx.diagnostics);
  const replay = useScenarioReplay(scenario);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [expecting, setExpecting] = useState<string | null>(null);
  // "Also record trigger points": Update expectations then also writes the points that ran at each step.
  const [alsoRecord, setAlsoRecord] = useState(false);
  // The expected values name the subject's attributes (the file keys them by id): the subject entity and the entities it is based on.
  // A scenario without a subject object still sets the process's own subjects' attributes, so their entities name the values then.
  const lookup = indexLookup(useIndex().data);
  const subjectEntity = scenario.objects?.find((o) => o.id === scenario.subject)?.entity;
  const starts = subjectEntity ? [subjectEntity] : subjectsOf(processDoc).flatMap((s) => (s.entity ? [s.entity] : []));
  const chain: string[] = [];
  for (const start of starts) for (let id: string | undefined = start; id && !chain.includes(id); id = lookup.byId.get(id)?.base) chain.push(id);
  const chainDocs = useElements(chain).byId;
  const valueNames = new Map<string, string>();
  for (const id of chain)
    for (const a of (chainDocs.get(id)?.json as { attributes?: { id: string; name: string }[] } | undefined)?.attributes ?? []) valueNames.set(a.id, a.name);
  const verbs = useVerbs();
  const objectFields = useObjectFields(starts);
  const usedRights = (processDoc?.rights ?? []).flatMap((r) => (r.right ? [r.right] : []));
  const namedRights = useElements(usedRights).byId;
  // The trigger points that can act on a step: this lifecycle's, and those of the other lifecycles that have some (an object verb or an own
  // verb also runs the points of every lifecycle that governs the object). Another lifecycle's are named `Lifecycle.Name`.
  const otherLifecycles = (useIndex().data ?? []).filter(
    (r) => r.kind === "process" && !!(r as { triggerPoints?: number }).triggerPoints && r.id !== scenario.process,
  );
  const otherDocs = useElements(otherLifecycles.map((r) => r.id)).byId;
  const allPoints = [
    ...(processDoc?.triggerPoints ?? []).map((t) => ({ t, label: t.name })),
    ...otherLifecycles.flatMap((r) =>
      ((otherDocs.get(r.id)?.json as unknown as ProcessDoc | undefined)?.triggerPoints ?? []).map((t) => ({ t, label: `${r.name}.${t.name}` })),
    ),
  ];
  // What a verb step's link and a refusal point at: relation ends, the process's rights (inline or named) and other lifecycles.
  const more = new Map<string, string>();
  for (const f of objectFields.fields) more.set(f.id, f.name);
  for (const r of processDoc?.rights ?? [])
    more.set(r.right ?? r.id, r.right ? String((namedRights.get(r.right)?.json as Rec | undefined)?.name ?? "?") : (r.name ?? "?"));
  // What can refuse a step: this lifecycle, its rights, events, gates and guards.
  const refusers: GridOption[] = [];
  if (processDoc) {
    refusers.push({ value: processDoc.id, label: processDoc.name });
    for (const r of processDoc.rights ?? []) {
      const rid = r.right ?? r.id;
      if (more.get(rid)) refusers.push({ value: rid, label: more.get(rid)! });
    }
    for (const e of processDoc.events ?? []) refusers.push({ value: e.id, label: e.name });
    for (const t of processDoc.transitions ?? []) if (t.gate) refusers.push({ value: t.gate.id, label: t.gate.displayName || t.gate.name });
    for (const g of processDoc.guards ?? []) refusers.push({ value: g.id, label: g.name });
    for (const { t, label } of allPoints) refusers.push({ value: t.id, label });
  }
  for (const o of refusers) more.set(o.value, o.label);
  // A refusing element that is in none of these and not in the model any more reads "(deleted)".
  for (const step of scenario.steps) {
    const id = step.expect?.refusedBy;
    if (id && !more.has(id)) {
      const found = lookup.byId.get(id);
      more.set(id, found ? found.displayName || found.name : RL.deleted);
    }
  }
  const rows = stepRows(scenario, processDoc, actorNames, valueNames, more);
  const verbOptions: GridOption[] = verbGroups("right", verbs.own, verbs.hidden)
    .filter((g) => g.id !== "move" && g.id !== "signature" && g.id !== "task")
    .flatMap((g) => g.options.map((o) => ({ value: o.key, label: o.label })));
  const endOptions: GridOption[] = objectFields.fields.filter((f) => f.kind === "end").map((f) => ({ value: f.id, label: f.name }));
  const attributeOptions: GridOption[] = objectFields.fields.filter((f) => f.kind === "attribute").map((f) => ({ value: f.id, label: f.name }));
  const objectOptions: GridOption[] = (scenario.objects ?? []).map((o) => ({ value: o.id, label: o.name }));
  const verbSteps = rows.some((r) => r.input === "verb");
  const triggers = !!processDoc?.triggerPoints?.length || rows.some((r) => r.checkWarnings || r.triggerPoints);
  const refusals = rows.some((r) => r.refusal || r.refusedBy) || verbSteps || !!processDoc?.rights?.length || triggers;
  // The team-code checks a step's Assume cell can set, each with Pass, Warning or Error (decision 26).
  const teamChecks = new Set(allPoints.filter(({ t }) => isTeamCheck(t)).map(({ t }) => t.id));
  const names = stepNames(processDoc, actorNames);
  const states = processDoc ? [...stateIndex(processDoc).values()].map((i) => ({ id: i.state.id, name: i.path })) : [];
  const events: GridOption[] = (processDoc?.events ?? []).map((e) => ({ value: e.id, label: e.name }));
  const invokes: GridOption[] = processDoc
    ? [...stateIndex(processDoc).values()].flatMap((i) => (i.state.invoke ?? []).map((x) => ({ value: x.id, label: x.name })))
    : [];
  const meanings: GridOption[] = (processDoc?.transitions ?? []).flatMap((t) => (t.gate?.meanings ?? []).map((m) => ({ value: m.id, label: m.name })));
  const none = { value: "", label: "(none)" };
  const step = (row: StepRow) => scenario.steps[row.index];

  const columns: GridColumn<StepRow>[] = [
    { key: "index", label: "#", kind: "readonly", width: "w-8", value: (r) => String(r.index + 1) },
    { key: "input", label: "Input", kind: "select", width: "w-24", value: (r) => r.input, options: () => STEP_INPUTS.map((i) => ({ value: i, label: i })) },
    {
      key: "on",
      label: "Event",
      kind: (r) => (r.input === "time" ? "text" : "select"),
      width: "min-w-28",
      value: (r) => (r.input === "verb" ? (describeVerb(r.verb, verbs.own)?.label ?? r.verb) : r.on),
      editValue: (r) =>
        r.input === "event"
          ? (step(r)?.event ?? "")
          : r.input === "time"
            ? (step(r)?.after ?? "")
            : r.input === "verb"
              ? (step(r)?.verb ?? "")
              : (step(r)?.invoke ?? ""),
      options: (r) => (r.input === "event" ? events : r.input === "verb" ? verbOptions : invokes),
    },
    ...(scenario.participants?.length
      ? [
          {
            key: "participant",
            label: PL.step,
            kind: "select" as const,
            width: "min-w-24",
            value: (r: StepRow) => r.participant,
            editValue: (r: StepRow) => step(r)?.participant ?? "",
            options: () => [none, ...(scenario.participants ?? []).map((p) => ({ value: p.id, label: p.name }))],
          },
        ]
      : []),
    {
      key: "actor",
      label: "Actor",
      kind: "select",
      width: "min-w-24",
      value: (r) => r.actor,
      editValue: (r) => step(r)?.actor ?? "",
      options: () => [none, ...actors.map((a) => ({ value: a.id, label: a.name }))],
    },
    ...(refusals
      ? [
          {
            key: "refusal",
            label: RL.refusal,
            tooltip: RL.expectedRefusalHint,
            kind: "select" as const,
            width: "w-24",
            value: (r: StepRow) => (r.refusal ? (RL.refusalWords[r.refusal] ?? r.refusal) : ""),
            editValue: (r: StepRow) => step(r)?.expect?.refusal ?? "",
            options: () => [{ value: "", label: RL.refusalNone }, ...Object.entries(RL.refusalWords).map(([value, label]) => ({ value, label }))],
          },
          {
            key: "refusedBy",
            label: RL.refusedBy,
            tooltip: RL.expectedRefusedByHint,
            kind: "select" as const,
            width: "w-28",
            value: (r: StepRow) => r.refusedBy,
            editValue: (r: StepRow) => step(r)?.expect?.refusedBy ?? "",
            options: (r: StepRow) => {
              const current = step(r)?.expect?.refusedBy;
              // A refuser that no longer exists stays in the list, as "(deleted)", until it is changed.
              return [
                { value: "", label: RL.refusalNone },
                ...refusers,
                ...(current && !refusers.some((o) => o.value === current) ? [{ value: current, label: more.get(current) ?? RL.deleted }] : []),
              ];
            },
          },
        ]
      : []),
    ...(triggers
      ? [
          {
            key: "checkWarnings",
            label: TL.checkWarnings,
            tooltip: TL.checkWarningsTip,
            kind: "pick" as const,
            width: "min-w-24",
            value: (r: StepRow) => r.checkWarnings,
            editValue: (r: StepRow) => (step(r)?.expect?.checkWarnings ?? []).join(","),
            options: () => allPoints.filter(({ t }) => t.type === "check").map(({ t, label }) => ({ value: t.id, label })),
          },
          {
            key: "triggerPoints",
            label: TL.expectedPoints,
            tooltip: TL.expectedPointsTip,
            kind: "readonly" as const,
            width: "min-w-28",
            value: (r: StepRow) => r.triggerPoints,
          },
        ]
      : []),
    ...(verbSteps
      ? [
          {
            key: "setValues",
            label: RL.stepValues,
            tooltip: RL.stepValuesHint,
            kind: "text" as const,
            width: "min-w-28",
            mono: true,
            value: (r: StepRow) => r.setValues,
          },
          {
            key: "attributes",
            label: RL.stepAttributes,
            tooltip: RL.stepAttributesTip,
            kind: "pick" as const,
            width: "min-w-24",
            value: (r: StepRow) => r.attributes,
            editValue: (r: StepRow) => (step(r)?.attributes ?? []).join(","),
            options: () => attributeOptions,
            // Only a read step names attributes (MQ9305).
            locked: (r: StepRow) => r.verb !== "read",
          },
          {
            key: "end",
            label: RL.linkEnd,
            kind: "select" as const,
            width: "min-w-24",
            value: (r: StepRow) => r.end,
            editValue: (r: StepRow) => step(r)?.end ?? "",
            options: () => [none, ...endOptions],
          },
          {
            key: "object",
            label: RL.linkObject,
            kind: "select" as const,
            width: "min-w-24",
            value: (r: StepRow) => r.object,
            editValue: (r: StepRow) => step(r)?.object ?? "",
            options: () => [none, ...objectOptions],
          },
        ]
      : []),
    {
      key: "states",
      label: "Expected states",
      kind: "multi",
      width: "min-w-32",
      value: (r) => r.states,
      options: () => states.map((s) => ({ value: s.id, label: s.name })),
    },
    { key: "context", label: "Expected context", kind: "text", width: "min-w-28", mono: true, value: (r) => r.context },
    {
      key: "values",
      label: EL.expectedValues,
      tooltip: EL.expectedValuesHint,
      kind: "readonly",
      width: "min-w-28",
      mono: true,
      value: (r) => r.values,
    },
    { key: "signer", label: "Signer", kind: "text", width: "min-w-20", value: (r) => r.signer },
    {
      key: "meaning",
      label: "Meaning",
      kind: "select",
      width: "min-w-20",
      value: (r) => r.meaning,
      editValue: (r) => step(r)?.meaning ?? "",
      options: () => [none, ...meanings],
    },
    { key: "reason", label: "Reason", kind: "text", width: "min-w-20", value: (r) => r.reason },
    { key: "payload", label: "Payload", kind: "text", width: "min-w-28", mono: true, value: (r) => r.payload },
    {
      key: "assume",
      label: "Assumptions",
      tooltip: TL.assumeTip,
      kind: "text",
      width: "min-w-24",
      mono: true,
      value: (r) => r.assume,
      // The team-code checks the step can reach and has no entry for: they pass unless the cell says Warning or Error.
      render: (r) => {
        const open = processDoc && step(r) ? reachableChecks(processDoc, step(r)!).filter((t) => !(step(r)?.assume && t.id in step(r)!.assume!)) : [];
        return (
          <span className="truncate" data-testid="assume-cell">
            {r.assume}
            {open.map((t) => (
              <span key={t.id} className="ml-1 text-secondary" title={`${t.name}: ${Object.values(CHECK_WORD_LABELS).join(", ")}`} data-testid="assume-check">
                {r.assume || open[0] !== t ? ", " : ""}
                {t.name}=pass
              </span>
            ))}
          </span>
        );
      },
    },
    {
      key: "status",
      label: "Replay",
      kind: "readonly",
      width: "w-20",
      value: (r) => replay.steps[r.index] ?? "not-run",
      render: (r) => {
        const s = replay.steps[r.index] ?? "not-run";
        return (
          <Badge tone={STATUS_TONE[s]} data-testid="step-status" data-status={s}>
            {s === "not-run" ? "not run" : s}
          </Badge>
        );
      },
    },
  ];

  const setOpt = (s: StepDoc, key: keyof StepDoc, value: unknown) => {
    const r = s as unknown as Rec;
    if (value === "" || value === undefined || (typeof value === "object" && value !== null && !Array.isArray(value) && !Object.keys(value).length))
      delete r[key];
    else r[key] = value;
  };

  const commit = (row: StepRow, key: string, value: string | boolean) => {
    const v = String(value);
    ctx.edit((j) => {
      const s = (j as unknown as ScenarioDoc).steps.find((x) => x.id === row.id);
      if (!s) return;
      if (key === "input") {
        setOpt(s, "input", v === "event" ? undefined : v);
        if (v !== "event") delete s.event;
        if (v !== "time") delete s.after;
        if (v !== "invoke-done" && v !== "invoke-error") delete s.invoke;
        if (v !== "verb") {
          delete s.verb;
          delete s.values;
          delete s.attributes;
          delete s.end;
          delete s.object;
        } else if (!s.verb && verbOptions[0]) s.verb = verbOptions.find((o) => o.value === "modify")?.value ?? verbOptions[0].value;
      } else if (key === "on") {
        setOpt(s, row.input === "event" ? "event" : row.input === "time" ? "after" : row.input === "verb" ? "verb" : "invoke", v.trim());
        if (row.input === "verb" && v.trim() !== "read") delete s.attributes;
      } else if (key === "refusal" || key === "refusedBy") {
        // A refusal expected is a refused step: the reason or the refuser also says so.
        const expect = { ...(s.expect ?? {}) } as NonNullable<StepDoc["expect"]>;
        if (v) {
          expect[key] = v;
          expect.accepted = false;
        } else delete expect[key];
        setOpt(s, "expect", expect);
      } else if (key === "end" || key === "object") setOpt(s, key, v.trim());
      else if (key === "attributes") setOpt(s, "attributes", v.split(",").filter(Boolean));
      else if (key === "setValues") setOpt(s, "values", parseValuesText(v, valueNames).values);
      else if (key === "participant" || key === "actor" || key === "meaning" || key === "signer" || key === "reason") setOpt(s, key, v.trim());
      else if (key === "payload") setOpt(s, "payload", parseMapText(v, names.attrs));
      else if (key === "assume") {
        setOpt(s, "assume", parseAssume(parseMapText(v, names.guards), teamChecks));
      } else if (key === "checkWarnings") {
        const expect = { ...(s.expect ?? {}) } as NonNullable<StepDoc["expect"]>;
        const ids = v.split(",").filter(Boolean);
        if (ids.length) expect.checkWarnings = ids;
        else delete expect.checkWarnings;
        setOpt(s, "expect", expect);
      } else if (key === "states" || key === "context") {
        const expect = { ...(s.expect ?? {}) };
        if (key === "states") {
          const ids = idsOfNames(v, states);
          if (ids.length) expect.states = ids;
          else delete expect.states;
        } else {
          const map = parseMapText(v, names.attrs);
          if (Object.keys(map).length) expect.context = map;
          else delete expect.context;
        }
        setOpt(s, "expect", expect);
      }
    });
    ctx.flush();
  };

  // The same as Shift+R on the Scenarios tab: the steps' expectations become what the replay does, then the replay runs again.
  const updateExpectations = async () => {
    setBusy(true);
    try {
      // One update: with "Also record trigger points" the same operation writes the points that ran (decision 28).
      const refused = await refreshScenario(qc, store, scenario.id, alsoRecord);
      if (refused) store.getState().notify(`${RL.updateExpectations}: ${refused}`, "error");
    } finally {
      setBusy(false);
    }
    setFailure(null);
    await replayNow();
  };

  const replayNow = async () => {
    setBusy(true);
    setFailure(null);
    try {
      const result = await verifyScenario(scenario.process, scenario.id);
      const r = result.results[0];
      // The engine words some findings with their own "Step N"; the step is named once, up front.
      if (r?.failure)
        setFailure(
          failureSentence(
            r.failure,
            scenario.steps[r.failure.step],
            (id) => more.get(id) ?? lookup.byId.get(id)?.displayName ?? lookup.byId.get(id)?.name ?? null,
          ),
        );
    } catch (e) {
      store.getState().notify(`Replay ${scenario.name}: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-2">
        <Button size="sm" onClick={() => void replayNow()} disabled={busy} data-testid="scenario-editor-replay">
          <Play /> Replay
        </Button>
        <ScenarioStatusBadge status={replay.status} failedAt={replay.failedAt} warnings={replay.warnings.length} />
        {!failure && replay.warnings.length ? (
          <span className="min-w-0 flex-1 whitespace-normal break-words text-12 text-warning" role="status" data-testid="replay-warnings">
            {replay.warnings.join(" ")}
          </span>
        ) : null}
        {failure ? (
          <span className="min-w-0 flex-1 whitespace-normal break-words text-12 text-danger" role="status" data-testid="replay-failure">
            {failure}
          </span>
        ) : null}
        {failure || triggers ? (
          <>
            {triggers ? (
              <label className="flex h-6 items-center gap-1 text-12" title={TL.alsoRecordTip}>
                <input type="checkbox" checked={alsoRecord} onChange={(e) => setAlsoRecord(e.target.checked)} data-testid="also-record-points" />
                {TL.alsoRecord}
              </label>
            ) : null}
            <Button
              size="sm"
              disabled={busy}
              onClick={() => void updateExpectations()}
              title={RL.updateExpectationsTip}
              data-testid="scenario-update-expectations"
            >
              {RL.updateExpectations}
            </Button>
          </>
        ) : null}
      </div>
      <Grid
        label={`Steps of ${scenario.name}`}
        testid="steps-grid"
        noun="step"
        rows={rows}
        columns={columns}
        rowName={(r) => `step ${r.index + 1}`}
        problems={(r) => rowProblems(problems, r.pointer)}
        onCommit={commit}
        actions={(row) => (
          <Button
            variant="ghost"
            size="icon-row"
            tabIndex={-1}
            label={EL.addExpected}
            onClick={() => setExpecting(row.id)}
            data-testid={`add-expected-${row.index + 1}`}
          >
            <Plus />
          </Button>
        )}
        onAdd={(after) => {
          ctx.edit((j) => {
            const steps = (j as unknown as ScenarioDoc).steps;
            const at = after ? steps.findIndex((s) => s.id === after.id) : -1;
            const next: StepDoc = { id: newId() };
            if (events[0]) next.event = events[0].value;
            steps.splice(at < 0 ? steps.length : at + 1, 0, next);
          });
          ctx.flush();
        }}
        onRemove={
          scenario.steps.length > 1
            ? (row) => {
                ctx.edit((j) => void ((j as unknown as ScenarioDoc).steps = (j as unknown as ScenarioDoc).steps.filter((s) => s.id !== row.id)));
                ctx.flush();
              }
            : undefined
        }
      />
      {expecting ? (
        <AddExpectedDialog
          names={valueNames}
          taken={new Set(Object.keys(scenario.steps.find((s) => s.id === expecting)?.expect?.values ?? {}))}
          onClose={() => setExpecting(null)}
          onAdd={(attribute, text) => {
            ctx.edit((j) => {
              const s = (j as unknown as ScenarioDoc).steps.find((x) => x.id === expecting);
              if (!s) return;
              const added = parseValuesText(`x=${text}`, new Map([[attribute, "x"]])).values;
              setOpt(s, "expect", { ...(s.expect ?? {}), values: { ...(s.expect?.values ?? {}), ...added } });
            });
            ctx.flush();
            setExpecting(null);
          }}
        />
      ) : null}
    </div>
  );
}

/** Add expected value: one attribute of the subject object (by name) and the value the object should hold after the step. */
function AddExpectedDialog({
  names,
  taken,
  onAdd,
  onClose,
}: {
  names: ReadonlyMap<string, string>;
  taken: ReadonlySet<string>;
  onAdd: (attribute: string, text: string) => void;
  onClose: () => void;
}) {
  const choices = [...names].filter(([id]) => !taken.has(id)).sort((a, b) => a[1].localeCompare(b[1]));
  const [attribute, setAttribute] = useState(choices[0]?.[0] ?? "");
  const [text, setText] = useState("");
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={EL.addExpected}>
        <form
          className="flex flex-col gap-2"
          data-testid="add-expected-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            if (attribute && text.trim()) onAdd(attribute, text.trim());
          }}
        >
          {!choices.length ? <p className="text-12 text-warning">{EL.noExpectable}</p> : null}
          <Field label={EL.expectedAttribute} htmlFor="expected-attribute">
            <Select id="expected-attribute" value={attribute} onChange={(e) => setAttribute(e.target.value)} disabled={!choices.length}>
              {choices.map(([id, name]) => (
                <option key={id} value={id}>
                  {name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label={EL.expectedValue} htmlFor="expected-value">
            <Input id="expected-value" autoFocus value={text} onChange={(e) => setText(e.target.value)} />
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!attribute || !text.trim()} data-testid="add-expected-apply">
              Add
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
