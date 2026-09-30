// The Gates tab (phase-3-design.md 6.2): one form per gated transition: the transition, required N of M (M is the
// signers' count), signers and required actors (actor pickers), allow repeat signer, reason required, the meanings
// grid and the audit attributes grid. A gate is added from its transition's row on the Transitions tab.
import { useState } from "react";
import { ShieldOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Field, Input } from "@/components/ui/input";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { ChipsEditor, TextField } from "@/inspector/fields";
import { gateForms, uniqueName, type GateDoc, type GateForm, type MeaningDoc, type ProcessDoc } from "@/model/process";
import { newId } from "@/lib/ids";
import { Grid, type GridColumn } from "./Grid";
import { ProcessAttributeGrid } from "./AttributeLists";
import { rowProblems, type ProcessContext } from "./shared";

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
  const problems = rowProblems(pc.problems, form.pointer);
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
      </div>
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
      <div className="flex flex-wrap gap-3">
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
      </div>
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
