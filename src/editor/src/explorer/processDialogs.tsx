// The Processes explorer's dialogs (phase-3-design.md 6.1): New process, New actor, New scenario, Import XState, and
// the delete of a process with its scenarios. Each saves as one batch: one change, one undo entry.
import { useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { applyBatchResult, elementQuery, keys, useElements, useProject } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ElementSummary, ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { ACTOR_TYPE_LABELS, GROUP_LABELS, PROCESS_LABELS } from "@/model/labels";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { domainChoices } from "./dialogs";
import type { Forest } from "./tree";
import { CREATE_LABELS, type CreateKind } from "./create";
import {
  ACTOR_TYPES,
  NEW_STATUS,
  buildActor,
  buildProcessBatch,
  buildScenario,
  enumAttributes,
  goalsStereotypes,
  importPreview,
  parseConfig,
  processNameProblem,
  processProblem,
  type ActorType,
  type BatchOp,
  type ProcessUse,
  type ScenarioStart,
} from "./processCreate";
import { importProcess, type ProcessImportResult } from "./processApi";

type Json = Record<string, unknown>;

/** Sends one batch; on success the cache takes the result and undo gets one entry. Returns an error message or null. */
export function useCommit() {
  const { store } = useServices();
  const qc = useQueryClient();
  return async (label: string, ops: BatchOp[], before: (Json | null)[]): Promise<string | null> => {
    const result = await endpoints.applyBatch({ operations: ops as never });
    if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
    if (result.outcome !== "saved") {
      const failed = result.items.find((i) => i.outcome !== "saved");
      return failed?.diagnostics[0]?.message ?? (failed?.referrers.length ? "It is still referenced." : result.outcome);
    }
    applyBatchResult(qc, result);
    store.getState().pushUndo({
      label,
      ids: ops.map((o) => ("id" in o ? o.id : String(o.element.id))),
      before: before.map((b) => (b ? (clone(b) as unknown as ModelJson) : null)),
      after: ops.map((o) => (o.op === "delete" ? null : (clone(o.element) as unknown as ModelJson))),
      afterHashes: ops.map((o, i) => (o.op === "delete" ? null : (result.items[i]?.hash ?? null))),
    });
    return null;
  };
}

function Actions({ onCancel, disabled, testid, label = "Create" }: { onCancel: () => void; disabled: boolean; testid: string; label?: string }) {
  return (
    <div className="flex justify-end gap-2">
      <Button type="button" onClick={onCancel}>
        Cancel
      </Button>
      <Button type="submit" variant="primary" disabled={disabled} data-testid={testid}>
        {label}
      </Button>
    </div>
  );
}

function ErrorLine({ error }: { error: string | null }) {
  return error ? (
    <p role="alert" className="text-12 text-danger">
      {error}
    </p>
  ) : null;
}

/** The dialog of a New process, New actor or New scenario request (store.newElement). */
export function ProcessCreateDialog({ kind, domain, source, forest }: { kind: CreateKind; domain: string | null; source?: string; forest: Forest }) {
  const { store } = useServices();
  const close = () => store.getState().requestNew(null);
  if (kind === "process") return <NewProcessDialog domain={domain} forest={forest} onClose={close} />;
  if (kind === "actor") return <NewActorDialog forest={forest} onClose={close} />;
  return <NewScenarioDialog process={source ?? null} forest={forest} onClose={close} />;
}

function useFinish(forest: Forest) {
  const { store } = useServices();
  const qc = useQueryClient();
  const { openEditor } = useEditorNavigation();
  return (id: string, kind: string, name: string) => {
    const summary =
      qc.getQueryData<ElementSummary[]>(keys.index)?.find((r) => r.id === id) ??
      ({ id, kind, name, tags: [], stereotypes: [], package: forest.byId.get(id)?.package ?? null } as unknown as ElementSummary);
    store.getState().setSidebar("processes");
    openEditor(summary, true);
    store.getState().notify(`Created ${name}.`);
  };
}

function NewProcessDialog({ domain, forest, onClose }: { domain: string | null; forest: Forest; onClose: () => void }) {
  const commit = useCommit();
  const finish = useFinish(forest);
  const [name, setName] = useState("");
  const [home, setHome] = useState(domain ?? "");
  const [use, setUse] = useState<ProcessUse>("lifecycle");
  const entities = useMemo(() => [...forest.byId.values()].filter((r) => r.kind === "entity").sort((a, b) => a.name.localeCompare(b.name)), [forest]);
  const [subject, setSubject] = useState(() => entities.find((e) => !home || e.package === home)?.id ?? "");
  const [attribute, setAttribute] = useState<string>("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const subjectDoc = useQuery({ ...elementQuery(subject), enabled: !!subject });
  const isEnum = (id: string) => forest.byId.get(id)?.kind === "enum";
  const attributes = enumAttributes(subjectDoc.data?.json as Json | undefined, isEnum);
  const chosen = attribute || attributes[0]?.id || (subjectDoc.data ? NEW_STATUS : "");
  const enumId = attributes.find((a) => a.id === chosen)?.enumId ?? "";
  const enumDoc = useQuery({ ...elementQuery(enumId), enabled: !!enumId });
  const previousId = use === "lifecycle" ? (subjectDoc.data?.json as Json | undefined)?.lifecycle : undefined;
  const previousDoc = useQuery({ ...elementQuery(typeof previousId === "string" ? previousId : ""), enabled: typeof previousId === "string" });
  const previousName = typeof previousId === "string" ? (forest.byId.get(previousId)?.name ?? previousId) : null;
  const members = ((enumDoc.data?.json as Json | undefined)?.members as Json[] | undefined)?.map((m) => String(m.name)) ?? [];
  const takenTypeNames = new Set(
    [...forest.byId.values()].filter((r) => ["entity", "enum", "value-object", "scalar-type"].includes(r.kind)).map((r) => r.name.toLowerCase()),
  );
  const input = {
    name,
    domain: home || null,
    use,
    subject: subject || null,
    subjectDoc: subjectDoc.data ? { json: subjectDoc.data.json as Json, hash: subjectDoc.data.hash } : null,
    attribute: use === "lifecycle" ? chosen : null,
    members,
    previousDoc: previousDoc.data ? { json: previousDoc.data.json as Json, hash: previousDoc.data.hash } : null,
    takenTypeNames,
  };
  const problem = processProblem(input);
  const save = async () => {
    if (problem || busy) return;
    setBusy(true);
    try {
      const { ops, processId } = buildProcessBatch(input, newId);
      const before = ops.map((o) =>
        o.op !== "update"
          ? null
          : o.id === subject && subjectDoc.data
            ? (subjectDoc.data.json as Json)
            : previousDoc.data && o.id === previousId
              ? (previousDoc.data.json as Json)
              : null,
      );
      const failed = await commit(`New process ${name.trim()}`, ops, before);
      if (failed) setError(failed);
      else {
        onClose();
        finish(processId, "process", name.trim());
      }
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={CREATE_LABELS.process}>
        <form
          className="flex flex-col gap-2"
          data-testid="new-process-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <Field label="Name" htmlFor="new-process-name">
            <Input
              id="new-process-name"
              autoFocus
              value={name}
              onChange={(e) => setName(e.target.value)}
              aria-invalid={(name !== "" && !!processNameProblem(name)) || undefined}
            />
          </Field>
          <Field label="Domain" htmlFor="new-process-domain">
            <Select id="new-process-domain" value={home} onChange={(e) => setHome(e.target.value)}>
              <option value="">{GROUP_LABELS.notInDomain}</option>
              {domainChoices(forest).map((c) => (
                <option key={c.id} value={c.id}>
                  {c.path}
                </option>
              ))}
            </Select>
          </Field>
          <fieldset className="flex items-center gap-3 text-12" aria-label="Use">
            <span className="text-secondary">Use</span>
            {(["lifecycle", "orchestration"] as const).map((u) => (
              <label key={u} className="flex h-6 items-center gap-1">
                <input type="radio" name="new-process-use" value={u} checked={use === u} onChange={() => setUse(u)} data-testid={`new-process-use-${u}`} />
                {u === "lifecycle" ? "Lifecycle" : "Orchestration"}
              </label>
            ))}
          </fieldset>
          <Field label={use === "lifecycle" ? "Subject entity" : "Subject entity (optional)"} htmlFor="new-process-subject">
            <Select
              id="new-process-subject"
              value={subject}
              onChange={(e) => {
                setSubject(e.target.value);
                setAttribute("");
              }}
            >
              {use === "orchestration" ? <option value="">None</option> : null}
              {entities.map((e) => (
                <option key={e.id} value={e.id}>
                  {e.displayName || e.name}
                </option>
              ))}
            </Select>
          </Field>
          {use === "lifecycle" && subject ? (
            <>
              <Field label="Bound attribute" htmlFor="new-process-attribute" hint="An enum-typed attribute: its members are the lifecycle's states.">
                <Select id="new-process-attribute" value={chosen} onChange={(e) => setAttribute(e.target.value)}>
                  {attributes.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.name}
                    </option>
                  ))}
                  <option value={NEW_STATUS}>{PROCESS_LABELS.newStatus}</option>
                </Select>
              </Field>
              {previousName ? (
                <p className="text-12 text-secondary" data-testid="new-process-previous">
                  {`${forest.byId.get(subject)?.name ?? "The entity"}'s lifecycle is ${previousName}; it becomes an orchestration.`}
                </p>
              ) : null}
            </>
          ) : null}
          <p className="text-12 text-secondary">
            {use === "lifecycle" && chosen !== NEW_STATUS && members.length
              ? `Starts with ${members.length} states, one per member.`
              : `Starts with one state, ${PROCESS_LABELS.initialState}.`}
          </p>
          {name !== "" && problem ? <p className="text-12 text-danger">{problem}</p> : null}
          <ErrorLine error={error} />
          <Actions onCancel={onClose} disabled={!!problem || busy} testid="new-process-create" />
        </form>
      </DialogContent>
    </Dialog>
  );
}

function NewActorDialog({ forest, onClose }: { forest: Forest; onClose: () => void }) {
  const commit = useCommit();
  const finish = useFinish(forest);
  const project = useProject();
  const [name, setName] = useState("");
  const [type, setType] = useState<ActorType>("person");
  const [chosen, setChosen] = useState<string[]>([]);
  const [goals, setGoals] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const stereotypeIds = useMemo(() => [...forest.byId.values()].filter((r) => r.kind === "stereotype").map((r) => r.id), [forest]);
  const docs = useElements(stereotypeIds);
  const stereotypes = stereotypeIds
    .map((id) => docs.byId.get(id)?.json as Json | undefined)
    .filter((s): s is Json => !!s && Array.isArray(s.appliesTo) && (s.appliesTo as string[]).includes("actor"))
    .map((s) => ({ key: String(s.key), name: String(s.name ?? s.key) }));
  const withGoals = goalsStereotypes((project.data?.extensions ?? []) as never);
  const showGoals = chosen.some((k) => withGoals.has(k));
  const problem = processNameProblem(name);
  const save = async () => {
    if (problem || busy) return;
    setBusy(true);
    try {
      const actor = buildActor({ name, type, stereotypes: chosen, goals: showGoals ? goals : "" }, newId);
      const failed = await commit(`New actor ${name.trim()}`, [{ op: "create", element: actor }], [null]);
      if (failed) setError(failed);
      else {
        onClose();
        finish(String(actor.id), "actor", name.trim());
      }
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={CREATE_LABELS.actor}>
        <form
          className="flex flex-col gap-2"
          data-testid="new-actor-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <Field label="Name" htmlFor="new-actor-name">
            <Input id="new-actor-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label="Type" htmlFor="new-actor-type">
            <Select id="new-actor-type" value={type} onChange={(e) => setType(e.target.value as ActorType)}>
              {ACTOR_TYPES.map((t) => (
                <option key={t} value={t}>
                  {ACTOR_TYPE_LABELS[t]}
                </option>
              ))}
            </Select>
          </Field>
          {stereotypes.length ? (
            <fieldset className="flex flex-wrap gap-3 text-12" aria-label="Stereotypes">
              {stereotypes.map((s) => (
                <label key={s.key} className="flex h-6 items-center gap-1">
                  <input
                    type="checkbox"
                    checked={chosen.includes(s.key)}
                    onChange={(e) => setChosen((c) => (e.target.checked ? [...c, s.key] : c.filter((x) => x !== s.key)))}
                    data-testid={`new-actor-stereotype-${s.key}`}
                  />
                  {s.name}
                </label>
              ))}
            </fieldset>
          ) : null}
          {showGoals ? (
            <Field label="Goals" htmlFor="new-actor-goals" hint="One per line.">
              <Textarea id="new-actor-goals" rows={3} value={goals} onChange={(e) => setGoals(e.target.value)} />
            </Field>
          ) : null}
          {name !== "" && problem ? <p className="text-12 text-danger">{problem}</p> : null}
          <ErrorLine error={error} />
          <Actions onCancel={onClose} disabled={!!problem || busy} testid="new-actor-create" />
        </form>
      </DialogContent>
    </Dialog>
  );
}

function NewScenarioDialog({ process: preset, forest, onClose }: { process: string | null; forest: Forest; onClose: () => void }) {
  const commit = useCommit();
  const finish = useFinish(forest);
  const processes = useMemo(() => [...forest.byId.values()].filter((r) => r.kind === "process").sort((a, b) => a.name.localeCompare(b.name)), [forest]);
  const [process, setProcess] = useState(preset && forest.byId.get(preset)?.kind === "process" ? preset : (processes[0]?.id ?? ""));
  const [name, setName] = useState("");
  const [start, setStart] = useState<ScenarioStart>("empty");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const doc = useQuery({ ...elementQuery(process), enabled: !!process });
  const problem = processNameProblem(name) ?? (!process ? "Choose a process." : !doc.data ? "Loading the process…" : null);
  const save = async () => {
    if (problem || busy || start !== "empty" || !doc.data) return;
    setBusy(true);
    try {
      const scenario = buildScenario(name, doc.data.json as Json, newId);
      const failed = await commit(`New scenario ${name.trim()}`, [{ op: "create", element: scenario }], [null]);
      if (failed) setError(failed);
      else {
        onClose();
        finish(String(scenario.id), "scenario", name.trim());
      }
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={CREATE_LABELS.scenario}>
        <form
          className="flex flex-col gap-2"
          data-testid="new-scenario-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <Field label="Process" htmlFor="new-scenario-process">
            <Select id="new-scenario-process" value={process} onChange={(e) => setProcess(e.target.value)}>
              {processes.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.displayName || p.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Name" htmlFor="new-scenario-name">
            <Input id="new-scenario-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <fieldset className="flex flex-col gap-1 text-12" aria-label="Start">
            <label className="flex h-6 items-center gap-2 text-disabled" title={PROCESS_LABELS.simulateLater}>
              <input
                type="radio"
                name="new-scenario-start"
                disabled
                checked={start === "record"}
                onChange={() => setStart("record")}
                data-testid="new-scenario-record"
              />
              {PROCESS_LABELS.recordFromSimulation}
              <span className="text-secondary">({PROCESS_LABELS.simulateLater.toLowerCase()})</span>
            </label>
            <label className="flex h-6 items-center gap-2">
              <input type="radio" name="new-scenario-start" checked={start === "empty"} onChange={() => setStart("empty")} data-testid="new-scenario-empty" />
              {PROCESS_LABELS.empty}
              <span className="text-secondary">(one step to fill in)</span>
            </label>
          </fieldset>
          {name !== "" && problem ? <p className="text-12 text-danger">{problem}</p> : null}
          <ErrorLine error={error} />
          <Actions onCancel={onClose} disabled={!!problem || busy} testid="new-scenario-create" />
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Import XState… on a domain: the config text or file, a dry run preview, then Apply. */
export function ImportXStateDialog({ domain, forest, onClose }: { domain: string | null; forest: Forest; onClose: () => void }) {
  const { store } = useServices();
  const qc = useQueryClient();
  const finish = useFinish(forest);
  const [text, setText] = useState("");
  const [preview, setPreview] = useState<ProcessImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const parsed = parseConfig(text);
  const request = "config" in parsed ? { config: parsed.config as never, package: domain } : null;
  const run = async (dryRun: boolean) => {
    if (!request || busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await importProcess(request, dryRun);
      if (dryRun) setPreview(result);
      else if (!result.applied || !result.id) setError(result.diagnostics[0]?.message ?? "The import was not applied.");
      else {
        await qc.invalidateQueries({ queryKey: keys.index });
        const created = result.document as Json | null;
        store.getState().pushUndo({
          label: `Import ${String(created?.name ?? "process")}`,
          ids: [result.id],
          before: [null],
          after: [clone(created) as unknown as ModelJson],
          afterHashes: [result.hash],
        });
        onClose();
        finish(result.id, "process", String(created?.name ?? ""));
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  const lines = preview ? importPreview(preview) : null;
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={PROCESS_LABELS.importXState.replace("…", "")}
        description={`Into ${domain ? (forest.byId.get(domain)?.name ?? domain) : GROUP_LABELS.notInDomain}`}
      >
        <form
          className="flex flex-col gap-2"
          data-testid="import-xstate-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void run(!preview);
          }}
        >
          <Field label="Machine config" htmlFor="import-xstate-config" hint="Paste the JSON config, or choose a file.">
            <Textarea
              id="import-xstate-config"
              rows={8}
              className="font-mono text-12"
              value={text}
              onChange={(e) => {
                setText(e.target.value);
                setPreview(null);
              }}
            />
          </Field>
          <input
            type="file"
            accept=".json,application/json"
            aria-label="Config file"
            className="text-12"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file)
                void file.text().then((t) => {
                  setText(t);
                  setPreview(null);
                });
            }}
          />
          {text && "problem" in parsed ? <p className="text-12 text-danger">{parsed.problem}</p> : null}
          {lines ? (
            <div className="rounded-control border border-default p-2 text-12" data-testid="import-xstate-preview">
              <p>
                Creates {lines.created} · removes {lines.removed} · {lines.errors} errors · {lines.warnings} warnings
              </p>
              <p className="text-secondary">States: {lines.states.join(", ") || "none"}</p>
              <p className="text-secondary">Events: {lines.events.join(", ") || "none"}</p>
              {preview!.diagnostics.map((d, i) => (
                <p key={i} className={d.severity === "error" ? "text-danger" : "text-secondary"}>
                  {d.rule}: {d.message}
                </p>
              ))}
            </div>
          ) : null}
          <ErrorLine error={error} />
          <Actions
            onCancel={onClose}
            disabled={!request || busy || (!!lines && lines.errors > 0)}
            testid={preview ? "import-xstate-apply" : "import-xstate-preview-button"}
            label={preview ? "Apply" : "Preview"}
          />
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Delete a process: its scenarios are deleted with it, in the same batch (said here). */
export function DeleteProcessDialog({ name, scenarios, onClose, onDelete }: { name: string; scenarios: number; onClose: () => void; onDelete: () => void }) {
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={`Delete ${name}?`}
        description={scenarios ? `Its ${scenarios === 1 ? "scenario is" : `${scenarios} scenarios are`} deleted with it.` : "It has no scenarios."}
      >
        <div className="flex justify-end gap-2" data-testid="delete-process-dialog">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="button" variant="danger" onClick={onDelete} data-testid="confirm-delete">
            Delete
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
