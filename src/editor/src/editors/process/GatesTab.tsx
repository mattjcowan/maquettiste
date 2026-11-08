// The Gates tab (phase-3-design.md 6.2): one form per gated transition: the transition, required N of M (M is the
// signers' count), signers and required actors (actor pickers), allow repeat signer, reason required, the meanings
// grid and the audit attributes grid. A gate is added from its transition's row on the Transitions tab. Use named signatures
// turns a gate into an approval (signoff-design.md section 11): a stage and a grid of named signatures in place of the one
// list of signers, with the gate keeping its meanings, reason and audit attributes.
import { useState } from "react";
import { ShieldOff } from "lucide-react";
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
  canRevert,
  editSignature,
  notByVerbKeys,
  refusal,
  removeSignature,
  revertToSigners,
  setNotBy,
  signatureRows,
  signaturesOf,
  stageOf,
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

type Dialogs = { kind: "when" | "notBy"; id: string } | null;

/** An approval's stage name and its grid of named signatures (signoff-design.md section 11). */
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
      width: "w-24",
      clip: "max-w-24",
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
      width: "w-28",
      clip: "max-w-28",
      mono: true,
      value: (r) => r.when || ALWAYS,
      onOpen: (r) => setDialog({ kind: "when", id: r.id }),
    },
    {
      key: "notBy",
      label: A.columns.notBy,
      tooltip: A.tips.notBy,
      kind: "popover",
      width: "w-24",
      clip: "max-w-24",
      value: (r) => r.notBy,
      onOpen: (r) => setDialog({ kind: "notBy", id: r.id }),
    },
    { key: "why", label: A.columns.why, tooltip: A.tips.why, kind: "text", width: "min-w-28", clip: "max-w-32", value: (r) => r.why },
  ];
  const stage = stageOf(g);
  return (
    <div className="flex flex-col gap-1">
      <SectionTitle>
        <span title={A.stageTip}>
          {A.signaturesNeeded} <span className="font-normal text-secondary">({A.stage.toLowerCase()} </span>
          <span className="font-normal text-secondary" data-testid="approval-stage">
            {stage?.name}
          </span>
          <span className="font-normal text-secondary">)</span>
        </span>
      </SectionTitle>
      {refused ? (
        <p role="alert" className="text-12 text-danger" data-testid="approval-refused">
          {refused}
        </p>
      ) : null}
      <Grid
        label={`Signatures of ${g.name}`}
        testid="signatures-grid"
        noun="signature"
        rows={rows}
        columns={columns}
        rowName={(r) => r.name}
        problems={(r) => rowProblems(pc.problems, r.pointer)}
        empty={A.empty}
        addLabel={A.add}
        onCommit={(r, key, value) => {
          const sig = signaturesOf(g).find((s) => s.id === r.id);
          const why = sig ? refusal(sig, key, value, { actors: actorOptions }) : null;
          setRefused(why);
          if (!why) set((x) => void editSignature(x, r.id, key, value, { actors: actorOptions }));
        }}
        // Always at the end: a new signature takes the signers of the last one and lands after it, wherever the cursor is.
        onAdd={() => set((x) => void addSignature(x))}
        onRemove={(r) => {
          const last = signaturesOf(g).length <= 1;
          setRefused(last ? A.lastSignature : null);
          if (!last) set((x) => void removeSignature(x, r.id));
        }}
      />
      {dialog ? <SignatureDialog dialog={dialog} pc={pc} form={form} moves={moves} set={set} onClose={() => setDialog(null)} /> : null}
    </div>
  );
}

/** The dialog behind Needed when and Not by of one signature. */
function SignatureDialog({
  dialog,
  pc,
  form,
  moves,
  set,
  onClose,
}: {
  dialog: NonNullable<Dialogs>;
  pc: ProcessContext;
  form: GateForm;
  moves: { value: string; label: string }[];
  set: SetGate;
  onClose: () => void;
}) {
  const verbs = useVerbs();
  const context = useSetContext(pc.process);
  const stage = (form.gate.stages ?? []).findIndex((s) => s.signatures.some((x) => x.id === dialog.id));
  const sig = form.gate.stages?.[stage]?.signatures.find((x) => x.id === dialog.id);
  if (!sig) return null;
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
