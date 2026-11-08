// The Gates tab (phase-3-design.md 6.2): one form per gated transition: the transition, required N of M (M is the
// signers' count), signers and required actors (actor pickers), allow repeat signer, reason required, the meanings
// grid and the audit attributes grid. A gate is added from its transition's row on the Transitions tab. Use named signatures
// turns a gate into an approval (signoff-design.md section 11): a stage and a grid of named signatures in place of the one
// list of signers, with the gate keeping its meanings, reason and audit attributes.
import { useState } from "react";
import { Plus, ShieldOff, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input } from "@/components/ui/input";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { ChipsEditor, TextField } from "@/inspector/fields";
import { treeText } from "@/model/effects";
import { APPROVAL_LABELS as A } from "@/model/labels";
import {
  addSignature,
  addStage,
  canRevert,
  editSignature,
  editStage,
  moveSignature,
  notByVerbKeys,
  refusal,
  removeSignature,
  removeStage,
  revertToSigners,
  roundGroups,
  roundOf,
  setNotBy,
  signatureRows,
  signaturesOf,
  stageRefusal,
  toApproval,
  ALWAYS,
  type SignatureRow,
} from "@/model/approvals";
import {
  gateForms,
  transitionLabel,
  uniqueName,
  type GateDoc,
  type GateForm,
  type Json,
  type MeaningDoc,
  type NamedSignatureDoc,
  type NotByDoc,
  type ProcessDoc,
} from "@/model/process";
import { describeVerb } from "@/model/verbList";
import { newId } from "@/lib/ids";
import { useVerbs } from "@/verbs/useVerbs";
import { Grid, type GridColumn } from "./Grid";
import { ProcessAttributeGrid } from "./AttributeLists";
import { useSetContext } from "./SetsEditor";
import { rowProblems, type ProcessContext } from "./shared";
import { ExpressionDialog } from "./TriggerPointsTab";

const gateOf = (p: ProcessDoc, transition: string): GateDoc | undefined => p.transitions?.find((t) => t.id === transition)?.gate;

export function GatesTab({ pc }: { pc: ProcessContext }) {
  const forms = gateForms(pc.process);
  if (!forms.length)
    return (
      <EmptyState title="No gates">A gate makes a transition wait for signatures. Add one from a transition on an event, on the Transitions tab.</EmptyState>
    );
  return (
    <div className="flex flex-col gap-3">
      {forms.map((f) => (
        <GateFormView key={f.transition} pc={pc} form={f} />
      ))}
    </div>
  );
}

export function GateFormView({ pc, form }: { pc: ProcessContext; form: GateForm }) {
  const { change, draftEdit, commit } = pc;
  const g = form.gate;
  const dom = `gate-${g.id}`;
  // The Required field keeps what is typed (an empty field while retyping) and saves once, on blur or Enter.
  const [requiredText, setRequiredText] = useState<string | null>(null);
  const endRequired = () => {
    setRequiredText(null);
    commit();
  };
  const actorOptions = pc.actors.map((a) => ({ value: a.id, label: a.name }));
  const set = (update: (gate: GateDoc) => void, save = true) => {
    const run = save ? change : draftEdit;
    run((p) => {
      const gate = gateOf(p, form.transition);
      if (gate) update(gate);
    });
  };
  const meaningColumns: GridColumn<MeaningDoc>[] = [
    { key: "name", label: "Name", kind: "text", width: "min-w-28", value: (m) => m.name },
    { key: "displayName", label: "Display name", kind: "text", width: "min-w-32", value: (m) => m.displayName ?? "" },
    { key: "description", label: "Description", kind: "text", width: "min-w-40", value: (m) => (typeof m.description === "string" ? m.description : "") },
  ];
  // A finding on a signature shows on its row of the grid, and once.
  const problems = rowProblems(
    pc.problems.filter((d) => !(d.jsonPointer ?? "").startsWith(`${form.pointer}/stages/`)),
    form.pointer,
  );
  const approval = form.approval;
  return (
    <section className="flex flex-col gap-2 rounded-control border border-default p-2" aria-label={`Gate ${g.displayName || g.name}`} data-testid="gate-form">
      <div className="flex items-center gap-2">
        <h3 className="text-13 font-semibold">{g.displayName || g.name}</h3>
        <span className="truncate text-12 text-secondary" data-testid="gate-transition">
          {form.transitionLabel}
        </span>
        <span className="flex-1" />
        <Button
          variant="ghost"
          size="icon-sm"
          label={`Remove the gate ${g.name}`}
          onClick={() => change((p) => void delete p.transitions?.find((t) => t.id === form.transition)?.gate)}
        >
          <ShieldOff />
        </Button>
      </div>
      {problems.length ? (
        <ul className="text-12 text-danger" data-testid="gate-problems">
          {problems.map((d, i) => (
            <li key={i}>
              <span className="font-mono">{d.rule}</span> {d.message}
            </li>
          ))}
        </ul>
      ) : null}
      <div className="grid grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] items-end gap-2">
        <TextField id={`${dom}-name`} label="Name" value={g.name} onChange={(v) => set((x) => void (x.name = v), false)} onBlur={commit} />
        {approval ? null : (
          <>
            <Field label={`Required (of ${form.of})`} htmlFor={`${dom}-required`}>
              <Input
                id={`${dom}-required`}
                type="number"
                min={1}
                value={requiredText ?? form.required}
                data-testid="gate-required"
                aria-describedby={`${dom}-of`}
                onChange={(e) => {
                  setRequiredText(e.target.value);
                  const n = Math.floor(Number(e.target.value));
                  if (!(n >= 1)) return;
                  set((x) => {
                    if (n === 1) delete x.required;
                    else x.required = n;
                  }, false);
                }}
                onBlur={endRequired}
                onKeyDown={(e) => {
                  if (e.key === "Enter") endRequired();
                }}
              />
              <span id={`${dom}-of`} className="text-11 text-secondary" data-testid="gate-of">
                {form.required} of {form.of} signers
              </span>
            </Field>
          </>
        )}
      </div>
      {approval ? null : (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(14rem,1fr))] gap-2">
          <ChipsEditor
            label="Signers"
            values={form.signers}
            options={actorOptions}
            allowFree={false}
            onChange={(values) =>
              set((x) => {
                x.signers = values;
                if (x.requiredActors) x.requiredActors = x.requiredActors.filter((a) => values.includes(a));
                if (!x.requiredActors?.length) delete x.requiredActors;
              })
            }
          />
          <ChipsEditor
            label="Required actors"
            values={form.requiredActors}
            options={actorOptions.filter((a) => form.signers.includes(a.value))}
            allowFree={false}
            onChange={(values) =>
              set((x) => {
                if (values.length) x.requiredActors = values;
                else delete x.requiredActors;
              })
            }
          />
        </div>
      )}
      <div className="flex flex-wrap gap-3">
        {approval ? null : (
          <CheckboxField
            id={`${dom}-repeat`}
            label="Allow repeat signer"
            checked={g.allowRepeatSigner === true}
            onChange={(v) =>
              set((x) => {
                if (v) x.allowRepeatSigner = true;
                else delete x.allowRepeatSigner;
              })
            }
          />
        )}
        <CheckboxField
          id={`${dom}-reason`}
          label="Reason required"
          checked={g.reasonRequired === true}
          onChange={(v) =>
            set((x) => {
              if (v) x.reasonRequired = true;
              else delete x.reasonRequired;
            })
          }
        />
        {approval ? (
          <span title={A.acrossSignaturesTip}>
            <CheckboxField
              id={`${dom}-across`}
              label={A.acrossSignatures}
              checked={g.allowSignerAcrossSignatures === true}
              onChange={(v) =>
                set((x) => {
                  if (v) x.allowSignerAcrossSignatures = true;
                  else delete x.allowSignerAcrossSignatures;
                })
              }
            />
          </span>
        ) : null}
      </div>
      {approval ? <ApprovalSection pc={pc} form={form} set={set} /> : null}
      <ConvertButton form={form} set={set} />
      <SectionTitle>Meanings</SectionTitle>
      <Grid
        label={`Meanings of ${g.name}`}
        testid="meanings-grid"
        noun="meaning"
        rows={g.meanings ?? []}
        columns={meaningColumns}
        rowName={(m) => m.name}
        onCommit={(row, key, value) =>
          set((x) => {
            const m = x.meanings?.find((y) => y.id === row.id);
            if (!m) return;
            const v = String(value).trim();
            if (key === "name") m.name = v || m.name;
            else if (v) (m as unknown as Record<string, unknown>)[key] = v;
            else delete (m as unknown as Record<string, unknown>)[key];
          })
        }
        onAdd={() =>
          set(
            (x) =>
              void (x.meanings = [
                ...(x.meanings ?? []),
                {
                  id: newId(),
                  name: uniqueName(
                    (x.meanings ?? []).map((m) => m.name),
                    "meaning",
                  ),
                },
              ]),
          )
        }
        onRemove={(row) => set((x) => void (x.meanings = (x.meanings ?? []).filter((m) => m.id !== row.id)))}
      />
      <SectionTitle>Audit attributes</SectionTitle>
      <ProcessAttributeGrid
        pc={pc}
        label={`Audit attributes of ${g.name}`}
        owner={g.id}
        pointerBase={`${form.pointer}/auditAttributes`}
        at={{
          get: (p) => gateOf(p, form.transition)?.auditAttributes,
          set: (p, list) => {
            const gate = gateOf(p, form.transition);
            if (!gate) return;
            if (list) gate.auditAttributes = list;
            else delete gate.auditAttributes;
          },
        }}
      />
    </section>
  );
}

type SetGate = (update: (gate: GateDoc) => void, save?: boolean) => void;

/** Use named signatures, or Back to one list of signers: the two ways across, one change each. */
function ConvertButton({ form, set }: { form: GateForm; set: SetGate }) {
  if (!form.approval)
    return (
      <div className="flex">
        <Button size="sm" title={A.useTip} onClick={() => set((x) => void toApproval(x))} data-testid="use-named-signatures">
          {A.use}
        </Button>
      </div>
    );
  const can = canRevert(form.gate);
  return (
    <div className="flex flex-col items-start gap-1">
      <Button
        size="sm"
        title={can ? A.backTip : A.backBlocked}
        disabled={!can}
        aria-describedby={can ? undefined : "back-to-signers-why"}
        onClick={() => set((x) => void revertToSigners(x))}
        data-testid="back-to-signers"
      >
        {A.back}
      </Button>
      {can ? null : (
        <p id="back-to-signers-why" className="text-12 text-secondary" data-testid="back-to-signers-why">
          {A.backWhy}
        </p>
      )}
    </div>
  );
}

type Dialogs = { kind: "when" | "notBy" | "details"; id: string } | { kind: "stageWhen"; id: string } | null;

/**
 * An approval's stages, by round, each with its grid of named signatures (signoff-design.md section 11). Stages of one round sit one
 * under the other under the round's heading, so the grid never widens past a signature's columns.
 */
function ApprovalSection({ pc, form, set }: { pc: ProcessContext; form: GateForm; set: SetGate }) {
  const { process, actors, actorNames } = pc;
  const verbs = useVerbs();
  const context = useSetContext(process);
  const g = form.gate;
  const [dialog, setDialog] = useState<Dialogs>(null);
  // An edit the grid refused (an actor no one has, a count below one), said at the grid; the cell keeps what it had.
  const [refused, setRefused] = useState<string | null>(null);
  const moves = (process.transitions ?? []).map((t) => ({ value: t.id, label: transitionLabel(process, t) }));
  const moveName = new Map(moves.map((m) => [m.value, m.label]));
  const rows = signatureRows(g, form.pointer, {
    actor: (id) => actorNames.get(id) ?? "?",
    transition: (id) => moveName.get(id) ?? "?",
    verb: (key) => describeVerb(key, verbs.own)?.label ?? key,
    condition: (tree) => treeText(tree, context.names),
  });
  const actorOptions = actors.map((a) => ({ id: a.id, name: a.name }));
  const names = (ids: string[]) => ids.map((a) => actorNames.get(a) ?? "").join(", ");
  const columns: GridColumn<SignatureRow>[] = [
    { key: "name", label: A.columns.name, kind: "text", width: "w-20", clip: "max-w-20", sticky: true, value: (r) => r.name },
    {
      key: "signers",
      label: A.columns.signers,
      tooltip: A.tips.signers,
      kind: "multi",
      width: "w-20",
      clip: "max-w-20",
      value: (r) => r.signers,
      editValue: (r) => names(r.signerIds),
      options: () => actorOptions.map((a) => ({ value: a.id, label: a.name })),
    },
    { key: "required", label: A.columns.required, tooltip: A.tips.required, kind: "text", width: "w-12", value: (r) => String(r.required) },
    {
      key: "requiredActors",
      label: A.columns.requiredActors,
      tooltip: A.tips.requiredActors,
      kind: "multi",
      width: "w-20",
      clip: "max-w-20",
      value: (r) => r.requiredActors,
      editValue: (r) => names(r.requiredActorIds),
      options: (r) => actorOptions.filter((a) => r.signerIds.includes(a.id)).map((a) => ({ value: a.id, label: a.name })),
    },
    {
      key: "repeat",
      label: A.columns.repeat,
      tooltip: A.tips.repeat,
      kind: "toggle",
      named: true,
      width: "w-12",
      value: (r) => (r.repeat ? "yes" : ""),
      on: (r) => r.repeat,
    },
    {
      key: "when",
      label: A.columns.when,
      tooltip: A.tips.when,
      kind: "popover",
      width: "w-20",
      clip: "max-w-20",
      mono: true,
      value: (r) => r.when || ALWAYS,
      cellTitle: (r) => r.when || ALWAYS,
      onOpen: (r) => setDialog({ kind: "when", id: r.id }),
    },
    {
      key: "notBy",
      label: A.columns.notBy,
      tooltip: A.tips.notBy,
      kind: "popover",
      width: "w-20",
      clip: "max-w-20",
      value: (r) => r.notBy,
      onOpen: (r) => setDialog({ kind: "notBy", id: r.id }),
    },
    {
      key: "details",
      label: A.details,
      tooltip: A.detailsTip,
      kind: "popover",
      width: "w-16",
      clip: "max-w-16",
      value: (r) => r.detailsShort,
      cellTitle: (r) => r.details,
      onOpen: (r) => setDialog({ kind: "details", id: r.id }),
    },
    {
      key: "why",
      label: A.columns.why,
      tooltip: A.tips.why,
      kind: "text",
      width: "min-w-20",
      clip: "max-w-28",
      value: (r) => r.why,
      cellTitle: (r) => r.why || undefined,
    },
  ];
  const groups = roundGroups(g);
  const stageIndex = (id: string) => (g.stages ?? []).findIndex((s) => s.id === id);
  // A finding on the stage itself (its round, its Skip unless) shows at the stage; one on a signature shows on its row.
  const stageProblems = (id: string) =>
    pc.problems.filter((d) => {
      const p = d.jsonPointer ?? "";
      const at = `${form.pointer}/stages/${stageIndex(id)}`;
      return p === at || (p.startsWith(`${at}/`) && !p.startsWith(`${at}/signatures/`));
    });
  return (
    <div className="flex flex-col gap-1">
      <SectionTitle>
        <span title={A.stageTip}>{A.signaturesNeeded}</span>
      </SectionTitle>
      <p className="text-11 text-secondary" data-testid="approval-stage-hint">
        {A.stageHint}
      </p>
      {refused ? (
        <p role="alert" className="text-12 text-danger" data-testid="approval-refused">
          {refused}
        </p>
      ) : null}
      {groups.map((group) => (
        <div key={group.round} className="flex flex-col gap-1" data-testid="approval-round" data-round={group.round}>
          <h4 className="text-12 font-semibold text-secondary" title={A.roundTip}>
            {A.roundHeading(group.round)}
          </h4>
          {group.stages.map((stage) => {
            const own = stageProblems(stage.id);
            return (
              <div key={stage.id} className="flex flex-col gap-1 rounded-control border border-default p-1.5" data-testid="approval-stage-block">
                <div className="flex flex-wrap items-end gap-2">
                  <StageText
                    label={A.stageName}
                    testid="approval-stage"
                    width="w-32"
                    value={stage.name}
                    onCommit={(v) => {
                      const why = stageRefusal(g, stage.id, "name", v);
                      setRefused(why);
                      if (!why) set((x) => void editStage(x, stage.id, "name", v));
                    }}
                  />
                  <StageText
                    label={A.stageRound}
                    tip={A.stageRoundTip}
                    testid="stage-round"
                    width="w-14"
                    value={String(roundOf(stage))}
                    onCommit={(v) => {
                      const why = stageRefusal(g, stage.id, "round", v);
                      setRefused(why);
                      if (!why) set((x) => void editStage(x, stage.id, "round", v));
                    }}
                  />
                  <div className="flex flex-col gap-0.5" title={A.skipUnlessTip}>
                    <span className="text-11 text-secondary">{A.skipUnless}</span>
                    <Button
                      size="sm"
                      className="max-w-48 justify-start font-mono"
                      data-testid="stage-when"
                      aria-label={`${A.skipUnless}, ${stage.name}: ${stage.when ? treeText(stage.when, context.names) : A.never}`}
                      onClick={() => setDialog({ kind: "stageWhen", id: stage.id })}
                    >
                      <span className="truncate">{stage.when ? treeText(stage.when, context.names) : A.never}</span>
                    </Button>
                  </div>
                  <span className="flex-1" />
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    label={A.removeStage(stage.name, stage.signatures.length)}
                    title={(g.stages ?? []).length <= 1 ? A.removeLastStageTip : A.removeStageTip(stage.name, stage.signatures.length)}
                    disabled={(g.stages ?? []).length <= 1}
                    data-testid="remove-stage"
                    onClick={() => {
                      setRefused(null);
                      set((x) => void removeStage(x, stage.id));
                    }}
                  >
                    <Trash2 />
                  </Button>
                </div>
                {own.length ? (
                  <ul className="text-12 text-danger" data-testid="stage-problems">
                    {own.map((d, i) => (
                      <li key={i}>
                        <span className="font-mono">{d.rule}</span> {d.message}
                      </li>
                    ))}
                  </ul>
                ) : null}
                <Grid
                  label={`Signatures of ${stage.name} in ${g.name}`}
                  testid="signatures-grid"
                  noun="signature"
                  rows={rows.filter((r) => r.stage === stage.id)}
                  columns={columns}
                  rowName={(r) => r.name}
                  problems={(r) => rowProblems(pc.problems, r.pointer)}
                  empty={A.empty}
                  addLabel={groups.length > 1 || (g.stages ?? []).length > 1 ? A.addSignatureTo(stage.name) : A.add}
                  onCommit={(r, key, value) => {
                    const sig = signaturesOf(g).find((s) => s.id === r.id);
                    const why = sig ? refusal(sig, key, value, { actors: actorOptions }) : null;
                    setRefused(why);
                    if (!why) set((x) => void editSignature(x, r.id, key, value, { actors: actorOptions }));
                  }}
                  // A new signature takes the signers of the last one of its stage and lands after it, wherever the cursor is.
                  onAdd={() => set((x) => void addSignature(x, undefined, stage.id))}
                  onRemove={(r) => {
                    const last = signaturesOf(g).length <= 1;
                    setRefused(last ? A.lastSignature : null);
                    if (!last) set((x) => void removeSignature(x, r.id));
                  }}
                />
              </div>
            );
          })}
        </div>
      ))}
      <div className="flex">
        <Button size="sm" title={A.addStageTip} onClick={() => set((x) => void addStage(x))} data-testid="add-stage">
          <Plus /> {A.addStage}
        </Button>
      </div>
      {dialog ? <SignatureDialog dialog={dialog} pc={pc} form={form} moves={moves} set={set} actors={actorOptions} onClose={() => setDialog(null)} /> : null}
    </div>
  );
}

/** A small text field that keeps what is typed and saves once, on blur or Enter. */
function StageText({
  label,
  tip,
  testid,
  width,
  value,
  onCommit,
}: {
  label: string;
  tip?: string;
  testid: string;
  width: string;
  value: string;
  onCommit: (value: string) => void;
}) {
  const [text, setText] = useState<string | null>(null);
  const done = () => {
    const v = text;
    setText(null);
    if (v !== null && v !== value) onCommit(v);
  };
  return (
    <label className="flex flex-col gap-0.5" title={tip}>
      <span className="text-11 text-secondary">{label}</span>
      <Input
        className={`${width} h-6`}
        value={text ?? value}
        data-testid={testid}
        onChange={(e) => setText(e.target.value)}
        onBlur={done}
        onKeyDown={(e) => {
          if (e.key === "Enter") done();
          if (e.key === "Escape") setText(null);
        }}
      />
    </label>
  );
}

/** The dialog behind Needed when, Not by, Details and a stage's Skip unless. */
function SignatureDialog({
  dialog,
  pc,
  form,
  moves,
  set,
  actors,
  onClose,
}: {
  dialog: NonNullable<Dialogs>;
  pc: ProcessContext;
  form: GateForm;
  moves: { value: string; label: string }[];
  set: SetGate;
  actors: { id: string; name: string }[];
  onClose: () => void;
}) {
  const verbs = useVerbs();
  const context = useSetContext(pc.process);
  if (dialog.kind === "stageWhen") {
    const index = (form.gate.stages ?? []).findIndex((s) => s.id === dialog.id);
    const stage = form.gate.stages?.[index];
    if (!stage) return null;
    const stored = (stage.when as Json | undefined) ?? undefined;
    return (
      <ExpressionDialog
        testid="stage-when-dialog"
        title={A.skipUnlessTitle(stage.name)}
        hint={A.skipUnlessHint}
        placeholder={A.whenPlaceholder}
        initial={treeText(stored, context.names)}
        request={(text) =>
          text === undefined
            ? stored
              ? { element: pc.id, at: `${form.pointer}/stages/${index}/when` }
              : null
            : text.trim()
              ? { element: pc.id, site: "approval", text }
              : null
        }
        onApply={(text) => {
          set((x) => void editStage(x, stage.id, "when", text));
          onClose();
        }}
        onClose={onClose}
      />
    );
  }
  const stage = (form.gate.stages ?? []).findIndex((s) => s.signatures.some((x) => x.id === dialog.id));
  const sig = form.gate.stages?.[stage]?.signatures.find((x) => x.id === dialog.id);
  if (!sig) return null;
  if (dialog.kind === "details")
    return <DetailsDialog sig={sig} stageId={form.gate.stages![stage].id} gate={form.gate} actors={actors} set={set} onClose={onClose} />;
  if (dialog.kind === "when") {
    const stored = (sig.when as Json | undefined) ?? undefined;
    const index = form.gate.stages![stage].signatures.findIndex((x) => x.id === sig.id);
    return (
      <ExpressionDialog
        testid="signature-when"
        title={A.whenTitle(sig.name)}
        hint={A.whenHint}
        placeholder={A.whenPlaceholder}
        initial={treeText(stored, context.names)}
        // A stored condition previews at its place; text being typed is judged at the approval site of the lifecycle.
        request={(text) =>
          text === undefined
            ? stored
              ? { element: pc.id, at: `${form.pointer}/stages/${stage}/signatures/${index}/when` }
              : null
            : text.trim()
              ? { element: pc.id, site: "approval", text }
              : null
        }
        onApply={(text) => {
          set((x) => void editSignature(x, sig.id, "when", text, { actors: [] }));
          onClose();
        }}
        onClose={onClose}
      />
    );
  }
  return (
    <NotByDialog
      name={sig.name}
      entries={sig.notBy ?? []}
      process={pc.process}
      verbs={verbs.own}
      moves={moves}
      onApply={(entries) => {
        set((x) => void setNotBy(x, sig.id, entries));
        onClose();
      }}
      onClose={onClose}
    />
  );
}

/**
 * Details of one signature: May be delegated to, Remind after, Escalate after, Escalate to and Sign again with a password, and the
 * stage that holds it. Apply writes them as one change; a duration that is not ISO 8601 is refused with a message.
 */
function DetailsDialog({
  sig,
  stageId,
  gate,
  actors,
  set,
  onClose,
}: {
  sig: NamedSignatureDoc;
  stageId: string;
  gate: GateDoc;
  actors: { id: string; name: string }[];
  set: SetGate;
  onClose: () => void;
}) {
  const [delegateTo, setDelegateTo] = useState(sig.delegateTo ?? []);
  const [escalateTo, setEscalateTo] = useState(sig.escalateTo ?? []);
  const [remindAfter, setRemindAfter] = useState(sig.remindAfter ?? "");
  const [escalateAfter, setEscalateAfter] = useState(sig.escalateAfter ?? "");
  const [reauthenticate, setReauthenticate] = useState(sig.reauthenticate === true);
  const [stage, setStage] = useState(stageId);
  const [refused, setRefused] = useState<string | null>(null);
  const options = actors.map((a) => ({ value: a.id, label: a.name }));
  const nameList = (ids: string[]) => ids.map((id) => actors.find((a) => a.id === id)?.name ?? "").join(", ");
  const apply = () => {
    const why = refusal(sig, "remindAfter", remindAfter, { actors }) ?? refusal(sig, "escalateAfter", escalateAfter, { actors });
    setRefused(why);
    if (why) return;
    set((x) => {
      const o = { actors };
      editSignature(x, sig.id, "delegateTo", nameList(delegateTo), o);
      editSignature(x, sig.id, "remindAfter", remindAfter, o);
      editSignature(x, sig.id, "escalateTo", nameList(escalateTo), o);
      editSignature(x, sig.id, "escalateAfter", escalateAfter, o);
      editSignature(x, sig.id, "reauthenticate", reauthenticate, o);
      if (stage !== stageId) moveSignature(x, sig.id, stage);
    });
    onClose();
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={A.detailsTitle(sig.name)} description={A.detailsHint}>
        <form
          className="flex flex-col gap-2"
          data-testid="signature-details-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            apply();
          }}
        >
          <ChipsEditor label={A.delegateTo} values={delegateTo} options={options} allowFree={false} whenNone={A.none} onChange={setDelegateTo} />
          <div className="grid grid-cols-2 gap-2">
            <Field label={A.remindAfter} htmlFor="sig-remind" hint={A.remindAfterTip}>
              <Input
                id="sig-remind"
                value={remindAfter}
                placeholder={A.durationPlaceholder}
                data-testid="sig-remind"
                onChange={(e) => setRemindAfter(e.target.value)}
              />
            </Field>
            <Field label={A.escalateAfter} htmlFor="sig-escalate" hint={A.escalateAfterTip}>
              <Input id="sig-escalate" value={escalateAfter} placeholder="P2D" data-testid="sig-escalate" onChange={(e) => setEscalateAfter(e.target.value)} />
            </Field>
          </div>
          <ChipsEditor label={A.escalateTo} values={escalateTo} options={options} allowFree={false} whenNone={A.none} onChange={setEscalateTo} />
          <span title={A.reauthenticateTip}>
            <CheckboxField id="sig-reauth" label={A.reauthenticate} checked={reauthenticate} onChange={setReauthenticate} />
          </span>
          {(gate.stages ?? []).length > 1 ? (
            <Field label={A.moveTo} htmlFor="sig-stage" hint={A.moveToTip}>
              <select
                id="sig-stage"
                className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
                value={stage}
                data-testid="sig-stage"
                onChange={(e) => setStage(e.target.value)}
              >
                {(gate.stages ?? []).map((s) => (
                  <option key={s.id} value={s.id}>
                    {`${s.name} (${A.roundHeading(roundOf(s)).toLowerCase()})`}
                  </option>
                ))}
              </select>
            </Field>
          ) : null}
          {refused ? (
            <p role="alert" className="text-12 text-danger" data-testid="sig-details-refused">
              {refused}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" data-testid="signature-details-apply">
              Apply
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Not by: the verbs and moves in one popover. Apply writes them as one change. */
function NotByDialog({
  name,
  entries,
  process,
  verbs,
  moves,
  onApply,
  onClose,
}: {
  name: string;
  entries: NotByDoc[];
  process: ProcessDoc;
  verbs: Parameters<typeof describeVerb>[1];
  moves: { value: string; label: string }[];
  onApply: (entries: NotByDoc[]) => void;
  onClose: () => void;
}) {
  const [verbKeys, setVerbKeys] = useState(entries.flatMap((n) => (n.verb !== undefined ? [n.verb] : [])));
  const [transitions, setTransitions] = useState(entries.flatMap((n) => (n.transition !== undefined ? [n.transition] : [])));
  const verbOptions = [...new Set([...notByVerbKeys(process), ...verbKeys])].map((key) => ({ value: key, label: describeVerb(key, verbs)?.label ?? key }));
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={A.notByTitle(name)} description={A.notByHint}>
        <form
          className="flex flex-col gap-2"
          data-testid="signature-notby-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            onApply([...verbKeys.map((v) => ({ verb: v })), ...transitions.map((t) => ({ transition: t }))]);
          }}
        >
          <ChipsEditor label={A.notByVerbs} values={verbKeys} options={verbOptions} allowFree={false} whenNone={A.none} onChange={setVerbKeys} />
          <ChipsEditor label={A.notByMoves} values={transitions} options={moves} allowFree={false} whenNone={A.none} onChange={setTransitions} />
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" data-testid="signature-notby-apply">
              Apply
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
