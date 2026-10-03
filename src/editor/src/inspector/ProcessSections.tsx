// The inspector's sections for processes, actors and scenarios (phase-3-design.md 6.2). A process shows what is
// selected inside its editor: the State section (general, entry and exit, invokes, bound member, description,
// display name, properties) or the Transition section (trigger, source and targets, guard, actions, external, gate,
// the event's actors read-only); with nothing inside selected, the Process section (use, subject, bound attribute,
// Code generation hints, and Source: the import provenance and opaque statechart data, read-only JSON). An actor shows
// its type and what uses it; a scenario its process, outcome and status.
import { useIndex, useElement, useProject } from "@/api/queries";
import { CheckboxField } from "@/components/ui/checkbox";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { Badge, SectionTitle } from "@/components/ui/misc";
import { indexLookup } from "@/model/index";
import { KIND_LABELS } from "@/model/model";
import {
  initialOf,
  setInitial,
  setStateType,
  setTrigger,
  stateIndex,
  stateRows,
  STATE_TYPES,
  TRIGGERS,
  type ProcessDoc,
  type ScenarioDoc,
  type StateDoc,
  type StateType,
  type TransitionDoc,
  type Trigger,
} from "@/model/process";
import { useProcessSelection } from "@/editors/process/selection";
import { BindingFields, useBinding } from "@/editors/process/ProcessControls";
import { useProcessContext, type ProcessContext } from "@/editors/process/shared";
import { ScenarioStatusBadge } from "@/editors/process/ScenariosTab";
import { useScenarioReplay } from "@/editors/scenario/ScenarioEditor";
import { ActorTypeField, ActorUsesList } from "@/editors/actor/ActorEditor";
import { useCodeGenerationTab } from "@/editors/EditorFrame";
import { ChipsEditor, TextField, type FormProps } from "./fields";
import { applicableExtensions, SchemaForm } from "./SchemaForm";
import { PropertyBag } from "./PropertyBag";
import { declaredKeys, editProperties } from "./propertyBag";

type Rec = Record<string, unknown>;

/** True when a process's inspector shows a node section in place of the process's own fields. */
export function useProcessNodeShown(id: string, kind: string | undefined): boolean {
  const selection = useProcessSelection(id);
  return kind === "process" && !!selection;
}

export function ProcessInspectorSection(props: FormProps) {
  const pc = useProcessContext(props);
  const selection = useProcessSelection(props.id);
  const index = stateIndex(pc.process);
  if (selection?.kind === "state") {
    const info = index.get(selection.id);
    if (info) return <StateSection pc={pc} state={info.state} />;
  }
  if (selection?.kind === "transition") {
    const t = pc.process.transitions?.find((x) => x.id === selection.id);
    if (t) return <TransitionSection pc={pc} transition={t} />;
  }
  return <ProcessSection props={props} />;
}

function ProcessSection({ props }: { props: FormProps }) {
  const index = useIndex();
  const summary = indexLookup(index.data).byId.get(props.id);
  // The inspector shows the process's property bag under its own fields.
  const codeGeneration = useCodeGenerationTab({ ...props, kind: "process", summary, name: String((props.json as Rec).name ?? "") }, { propertyBag: false });
  const source = (props.json as Rec).source;
  return (
    <section className="flex flex-col gap-2" aria-label="Process" data-testid="inspector-process">
      <SectionTitle>Process</SectionTitle>
      <div className="grid grid-cols-1 gap-2">
        <BindingFields ctx={props} dom={`${props.id}-insp`} />
      </div>
      <SectionTitle>Code generation hints</SectionTitle>
      {codeGeneration.content}
      <SectionTitle>Source</SectionTitle>
      {source ? (
        <pre className="max-h-60 overflow-auto rounded-control border border-default bg-app p-1 font-mono text-11" data-testid="process-source">
          {JSON.stringify(source, null, 2)}
        </pre>
      ) : (
        <p className="text-12 text-secondary">Made in the editor: no import provenance.</p>
      )}
    </section>
  );
}

const setOpt = (r: Rec, key: string, value: unknown) => {
  if (value === undefined || value === "" || (Array.isArray(value) && !value.length)) delete r[key];
  else r[key] = value;
};

function StateSection({ pc, state }: { pc: ProcessContext; state: StateDoc }) {
  const { process, change, draftEdit, commit } = pc;
  const project = useProject();
  const dom = `${pc.id}-state`;
  const { members } = useBinding(process.subject, process.boundAttribute);
  const row = stateRows(process, members).find((r) => r.id === state.id);
  const info = stateIndex(process).get(state.id);
  const actionOptions = (process.actions ?? []).map((a) => ({ value: a.id, label: a.name }));
  const edit = (update: (s: StateDoc, p: ProcessDoc) => void, save = true) =>
    (save ? change : draftEdit)((p) => {
      const s = stateIndex(p).get(state.id)?.state;
      if (s) update(s, p);
    });
  const type = state.type ?? "atomic";
  const extensions = applicableExtensions(project.data?.extensions ?? [], "state", state.stereotypes ?? []);
  return (
    <section className="flex flex-col gap-2" aria-label={`State ${state.name}`} data-testid="inspector-state">
      <SectionTitle>State</SectionTitle>
      <p className="font-mono text-11 text-secondary">{info?.path}</p>
      <TextField id={`${dom}-name`} label="Name" value={state.name} onChange={(v) => edit((s) => void (s.name = v), false)} onBlur={commit} />
      <TextField
        id={`${dom}-display`}
        label="Display name"
        value={state.displayName ?? ""}
        onChange={(v) => edit((s) => setOpt(s as unknown as Rec, "displayName", v), false)}
        onBlur={commit}
      />
      <Field label="Type" htmlFor={`${dom}-type`}>
        <Select id={`${dom}-type`} value={type} onChange={(e) => edit((s) => setStateType(s, e.target.value as StateType))}>
          {STATE_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </Select>
      </Field>
      {type !== "history" ? (
        <CheckboxField
          id={`${dom}-initial`}
          label="Initial state of its parent"
          checked={initialOf(info?.parent ?? process) === state.id}
          onChange={(v) => v && edit((_, p) => setInitial(p, state.id))}
        />
      ) : (
        <Field label="History" htmlFor={`${dom}-history`}>
          <Select
            id={`${dom}-history`}
            value={state.history ?? "shallow"}
            onChange={(e) => edit((s) => setOpt(s as unknown as Rec, "history", e.target.value === "deep" ? "deep" : undefined))}
          >
            <option value="shallow">shallow</option>
            <option value="deep">deep</option>
          </Select>
        </Field>
      )}
      <SectionTitle>Entry and exit</SectionTitle>
      <ChipsEditor
        label="Entry actions"
        values={state.entry ?? []}
        options={actionOptions}
        allowFree={false}
        onChange={(v) => edit((s) => setOpt(s as unknown as Rec, "entry", v))}
      />
      <ChipsEditor
        label="Exit actions"
        values={state.exit ?? []}
        options={actionOptions}
        allowFree={false}
        onChange={(v) => edit((s) => setOpt(s as unknown as Rec, "exit", v))}
      />
      <SectionTitle>Invokes</SectionTitle>
      {state.invoke?.length ? (
        <ul className="flex flex-col gap-0.5 text-12" data-testid="state-invokes">
          {state.invoke.map((i) => (
            <li key={i.id}>
              <span className="font-medium">{i.name}</span> <span className="text-secondary">{i.type ?? "service"}</span>
              {i.actors?.length ? <span className="text-secondary"> · {i.actors.map((a) => pc.actorNames.get(a) ?? "?").join(", ")}</span> : null}
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-12 text-secondary">None.</p>
      )}
      {process.use === "lifecycle" && !info?.parent ? (
        <>
          <SectionTitle>Bound member</SectionTitle>
          <p className="text-12" data-testid="state-bound">
            {row?.bound || "(none)"} {row?.drift ? <Badge tone="danger">MQ9203</Badge> : null}
          </p>
        </>
      ) : null}
      <SectionTitle>Description</SectionTitle>
      <Textarea
        aria-label={`Description of ${state.name}`}
        value={typeof state.description === "string" ? state.description : ""}
        onChange={(e) => edit((s) => setOpt(s as unknown as Rec, "description", e.target.value), false)}
        onBlur={commit}
      />
      <p className="text-11 text-secondary">A state's display name is localizable; its translations are not edited in the editor yet.</p>
      <ChipsEditor
        label="Stereotypes"
        values={state.stereotypes ?? []}
        options={(state.stereotypes ?? []).map((v) => ({ value: v, label: `«${v}»` }))}
        allowFree
        onChange={(v) => edit((s) => setOpt(s as unknown as Rec, "stereotypes", v))}
      />
      {extensions.length ? (
        <>
          <SectionTitle>Custom properties</SectionTitle>
          <SchemaForm
            extensions={extensions}
            json={state as unknown as Rec as never}
            defaults={{}}
            onChange={(name, value) =>
              edit((s) => {
                const next = { ...(s.properties ?? {}) };
                if (value === undefined) delete next[name];
                else next[name] = value;
                if (Object.keys(next).length) s.properties = next;
                else delete s.properties;
              })
            }
          />
        </>
      ) : null}
      <PropertyBag
        idPrefix={dom}
        properties={state.properties}
        declared={declaredKeys(extensions)}
        onEdit={(e) => edit((s) => editProperties(s as unknown as Rec, e))}
      />
    </section>
  );
}

function TransitionSection({ pc, transition: t }: { pc: ProcessContext; transition: TransitionDoc }) {
  const { process, change } = pc;
  const dom = `${pc.id}-transition`;
  const index = stateIndex(process);
  const states = [...index.values()].map((i) => ({ value: i.state.id, label: i.path }));
  const actions = (process.actions ?? []).map((a) => ({ value: a.id, label: a.name }));
  const trigger = t.trigger ?? "event";
  const event = process.events?.find((e) => e.id === t.event);
  const edit = (update: (x: TransitionDoc, p: ProcessDoc) => void) =>
    change((p) => {
      const x = p.transitions?.find((y) => y.id === t.id);
      if (x) update(x, p);
    });
  return (
    <section className="flex flex-col gap-2" aria-label="Transition" data-testid="inspector-transition">
      <SectionTitle>Transition</SectionTitle>
      <Field label="Trigger" htmlFor={`${dom}-trigger`}>
        <Select id={`${dom}-trigger`} value={trigger} onChange={(e) => edit((x, p) => setTrigger(x, e.target.value as Trigger, p))}>
          {TRIGGERS.map((v) => (
            <option key={v} value={v}>
              {v}
            </option>
          ))}
        </Select>
      </Field>
      {trigger === "event" ? (
        <Field label="Event" htmlFor={`${dom}-event`}>
          <Select id={`${dom}-event`} value={t.event ?? ""} onChange={(e) => edit((x) => void (x.event = e.target.value))}>
            {(process.events ?? []).map((e) => (
              <option key={e.id} value={e.id}>
                {e.name}
              </option>
            ))}
          </Select>
        </Field>
      ) : trigger === "after" ? (
        <Field label="After (ISO 8601 duration)" htmlFor={`${dom}-after`}>
          <Input id={`${dom}-after`} defaultValue={t.after ?? ""} onBlur={(e) => edit((x) => void (x.after = e.target.value.trim()))} />
        </Field>
      ) : null}
      <Field label="Source" htmlFor={`${dom}-source`}>
        <Select id={`${dom}-source`} value={t.source} onChange={(e) => edit((x) => void (x.source = e.target.value))}>
          {states.map((s) => (
            <option key={s.value} value={s.value}>
              {s.label}
            </option>
          ))}
        </Select>
      </Field>
      <ChipsEditor
        label="Targets"
        values={t.targets ?? []}
        options={states}
        allowFree={false}
        onChange={(v) => edit((x) => setOpt(x as unknown as Rec, "targets", v))}
      />
      <Field label="Guard" htmlFor={`${dom}-guard`}>
        <Select id={`${dom}-guard`} value={t.guard ?? ""} onChange={(e) => edit((x) => setOpt(x as unknown as Rec, "guard", e.target.value))}>
          <option value="">(none)</option>
          {(process.guards ?? []).map((g) => (
            <option key={g.id} value={g.id}>
              {g.name}
            </option>
          ))}
        </Select>
      </Field>
      <ChipsEditor
        label="Actions"
        values={t.actions ?? []}
        options={actions}
        allowFree={false}
        onChange={(v) => edit((x) => setOpt(x as unknown as Rec, "actions", v))}
      />
      <CheckboxField
        id={`${dom}-external`}
        label="External (exits and re-enters the source)"
        checked={t.external === true}
        onChange={(v) => edit((x) => setOpt(x as unknown as Rec, "external", v || undefined))}
      />
      <SectionTitle>Gate</SectionTitle>
      <p className="text-12" data-testid="transition-gate">
        {t.gate
          ? `${t.gate.displayName || t.gate.name}: ${t.gate.required ?? 1} of ${t.gate.signers?.length ?? 0} signers (edit it on the Gates tab)`
          : trigger === "event"
            ? "None: add one from the transition's row."
            : "Only a transition on an event can have a gate."}
      </p>
      <SectionTitle>Actors</SectionTitle>
      <p className="text-12" data-testid="transition-actors">
        {event ? (event.actors?.length ? event.actors.map((a) => pc.actorNames.get(a) ?? "?").join(", ") : "Any actor") : "No event."}
      </p>
      <p className="text-11 text-secondary">From the event {event?.name ?? ""}: edit them on the Events tab.</p>
    </section>
  );
}

export function ActorInspectorSection(props: FormProps) {
  return (
    <section className="flex flex-col gap-2" aria-label="Actor" data-testid="inspector-actor">
      <SectionTitle>{KIND_LABELS.actor}</SectionTitle>
      <ActorTypeField ctx={props} dom={`${props.id}-insp`} />
      <ActorUsesList actor={props.id} />
    </section>
  );
}

export function ScenarioInspectorSection(props: FormProps) {
  const scenario = props.json as unknown as ScenarioDoc;
  const process = useElement(scenario.process).data?.json as { name?: string } | undefined;
  const replay = useScenarioReplay(scenario);
  return (
    <section className="flex flex-col gap-2" aria-label="Scenario" data-testid="inspector-scenario">
      <SectionTitle>{KIND_LABELS.scenario}</SectionTitle>
      <dl className="grid grid-cols-[max-content_1fr] gap-x-2 gap-y-0.5 text-12">
        <dt className="text-secondary">Process</dt>
        <dd>{process?.name ?? "?"}</dd>
        <dt className="text-secondary">Outcome</dt>
        <dd>{scenario.outcome ?? "active"}</dd>
        <dt className="text-secondary">Steps</dt>
        <dd>{scenario.steps?.length ?? 0}</dd>
        <dt className="text-secondary">Status</dt>
        <dd>
          <ScenarioStatusBadge status={replay.status} failedAt={replay.failedAt} />
        </dd>
      </dl>
    </section>
  );
}
