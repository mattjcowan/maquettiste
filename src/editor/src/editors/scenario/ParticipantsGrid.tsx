// The scenario editor's Participants and Objects grids (actors-design.md section 9): who acts in the scenario (name, display
// name, identity, the actor they stand for, what they hold, their record object), and each object's owner participant.
import { useMemo, useState } from "react";
import { Pencil, Plus, X } from "lucide-react";
import { useElement, useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { newId } from "@/lib/ids";
import { PARTICIPANT_LABELS as L } from "@/model/labels";
import {
  forgetParticipant,
  holdable,
  identityOf,
  newParticipantName,
  holdsText,
  participantRows,
  standsFor,
  type ActorChoice,
  type ParticipantRow,
} from "@/model/participants";
import { subjectsOf, type HoldingDoc, type ProcessDoc, type ScenarioDoc } from "@/model/process";
import type { EditorContext } from "../EditorFrame";
import { Grid, type GridColumn, type GridOption } from "../process/Grid";
import { rowProblems, useActors, useProblems } from "../process/shared";

/** The model's actors as the participant pickers read them. */
export function useActorChoices(): ActorChoice[] {
  const { actors } = useActors();
  return useMemo(() => actors.map((a) => ({ id: a.id, name: a.name, type: a.actorType, relative: a.relative !== undefined })), [actors]);
}

/** The ids of the participants and holdings of a file: the engine reports a finding about one under its own id. */
const partIds = (scenario: ScenarioDoc) => (scenario.participants ?? []).flatMap((p) => [p.id, ...(p.holds ?? []).map((h) => h.id)]);

export function ParticipantsGrid({ ctx, scenario }: { ctx: EditorContext; scenario: ScenarioDoc }) {
  const choices = useActorChoices();
  const problems = useProblems(ctx.id, ctx.diagnostics, partIds(scenario));
  const actorNames = new Map(choices.map((a) => [a.id, a.name]));
  const objects = scenario.objects ?? [];
  const rows = participantRows(scenario, actorNames);
  const none: GridOption = { value: "", label: L.none };
  const [holdsFor, setHoldsFor] = useState<string | null>(null);
  const holder = scenario.participants?.find((p) => p.id === holdsFor);

  const columns: GridColumn<ParticipantRow>[] = [
    { key: "name", label: L.name, kind: "text", width: "min-w-24", sticky: true, value: (r) => r.name },
    { key: "displayName", label: L.displayName, kind: "text", width: "min-w-24", value: (r) => r.displayName },
    { key: "identity", label: L.identity, tooltip: L.identityHint, kind: "text", width: "min-w-20", value: (r) => r.identity },
    {
      key: "actor",
      label: L.actor,
      tooltip: L.actorHint,
      kind: "select",
      width: "min-w-24",
      value: (r) => r.actor,
      editValue: (r) => scenario.participants?.[r.index]?.actor ?? "",
      options: () => [none, ...standsFor(choices).map((a) => ({ value: a.id, label: a.type === "system" ? L.automatic : a.name }))],
    },
    {
      key: "holds",
      label: L.holds,
      tooltip: L.holdsHint,
      kind: "readonly",
      width: "min-w-36",
      value: (r) => r.holds,
      render: (r) => (
        <span className="flex min-w-0 items-center gap-1">
          <span className="min-w-0 flex-1 truncate" title={r.holds}>
            {r.holds || L.none}
          </span>
          <Button variant="ghost" size="icon-row" label={L.editHolds(r.name)} onClick={() => setHoldsFor(r.id)} data-testid={`edit-holds-${r.name}`}>
            <Pencil />
          </Button>
        </span>
      ),
    },
    {
      key: "object",
      label: L.object,
      kind: "select",
      width: "min-w-24",
      value: (r) => r.object,
      editValue: (r) => scenario.participants?.[r.index]?.object ?? "",
      options: () => [none, ...objects.map((o) => ({ value: o.id, label: o.name }))],
    },
  ];

  const commit = (row: ParticipantRow, key: string, value: string | boolean) => {
    const v = String(value);
    ctx.edit((j) => {
      const p = (j as unknown as ScenarioDoc).participants?.find((x) => x.id === row.id);
      if (!p) return;
      if (key === "name") p.name = v.trim();
      else if (key === "displayName") {
        if (v.trim()) p.displayName = v.trim();
        else delete p.displayName;
      } else if (key === "identity") {
        const identity = identityOf(v);
        if (identity === undefined) delete p.identity;
        else p.identity = identity;
      } else if (key === "actor" || key === "object") {
        if (v) p[key] = v;
        else delete p[key];
      }
    });
    ctx.flush();
  };

  const saveHolds = (holds: HoldingDoc[]) => {
    ctx.edit((j) => {
      const p = (j as unknown as ScenarioDoc).participants?.find((x) => x.id === holdsFor);
      if (!p) return;
      if (holds.length) p.holds = holds;
      else delete p.holds;
    });
    ctx.flush();
    setHoldsFor(null);
  };

  return (
    <>
      <Grid
        label={L.grid(scenario.name)}
        testid="participants-grid"
        noun={L.singular}
        rows={rows}
        columns={columns}
        rowName={(r) => r.name}
        problems={(r) => rowProblems(problems, r.pointer)}
        onCommit={commit}
        empty={`No ${L.plural} yet. Add one to say who acts in the scenario.`}
        onAdd={() => {
          ctx.edit((j) => {
            const s = j as unknown as ScenarioDoc;
            s.participants = [...(s.participants ?? []), { id: newId(), name: newParticipantName(s) }];
          });
          ctx.flush();
        }}
        onRemove={(row) => {
          ctx.edit((j) => forgetParticipant(j as unknown as ScenarioDoc, row.id));
          ctx.flush();
        }}
      />
      {holder ? (
        <HoldsDialog
          participant={holder.name}
          holds={holder.holds ?? []}
          roles={holdable(choices)}
          objects={objects}
          onSave={saveHolds}
          onClose={() => setHoldsFor(null)}
        />
      ) : null}
    </>
  );
}

/** A participant's holdings, one row each: a role, group or organization, then an optional scenario object as the context. */
function HoldsDialog({
  participant,
  holds,
  roles,
  objects,
  onSave,
  onClose,
}: {
  participant: string;
  holds: HoldingDoc[];
  roles: { id: string; name: string }[];
  objects: { id: string; name: string }[];
  onSave: (holds: HoldingDoc[]) => void;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState<HoldingDoc[]>(holds);
  const set = (i: number, next: Partial<HoldingDoc>) => setDraft((d) => d.map((h, at) => (at === i ? { ...h, ...next } : h)));
  const text = holdsText(
    draft.filter((h) => h.actor),
    new Map(roles.map((r) => [r.id, r.name])),
    new Map(objects.map((o) => [o.id, o.name])),
  );
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={L.holdsTitle(participant)}>
        <form
          className="flex flex-col gap-2"
          data-testid="holds-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            onSave(draft.filter((h) => h.actor));
          }}
        >
          <p className="text-12 text-secondary">{L.holdsHint}</p>
          {!roles.length ? <p className="text-12 text-secondary">{L.noRoles}</p> : null}
          {draft.map((h, i) => (
            <div key={h.id} className="flex items-center gap-1" data-testid="holding-row">
              <Select aria-label={L.holdsRole} className="h-6 flex-1" value={h.actor} onChange={(e) => set(i, { actor: e.target.value })}>
                <option value="">{L.holdsPickRole}</option>
                {roles.map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.name}
                  </option>
                ))}
              </Select>
              <span className="text-12 text-secondary">{L.within}</span>
              <Select
                aria-label={L.holdsContext}
                className="h-6 flex-1"
                value={h.within ?? ""}
                onChange={(e) =>
                  setDraft((d) => d.map((x, at) => (at === i ? { id: x.id, actor: x.actor, ...(e.target.value ? { within: e.target.value } : {}) } : x)))
                }
              >
                <option value="">{L.holdsAnywhere}</option>
                {objects.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name}
                  </option>
                ))}
              </Select>
              <Button type="button" variant="ghost" size="icon-row" label={L.removeHolding} onClick={() => setDraft((d) => d.filter((_, at) => at !== i))}>
                <X />
              </Button>
            </div>
          ))}
          <div className="flex items-center gap-2">
            <Button type="button" onClick={() => setDraft((d) => [...d, { id: newId(), actor: "" }])} data-testid="add-holding">
              <Plus /> {L.addHolding}
            </Button>
            <span className="min-w-0 flex-1 truncate text-12 text-secondary" title={text}>
              {text.replace(/ in /g, ` ${L.within} `)}
            </span>
          </div>
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" data-testid="holds-apply">
              Apply
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

interface ObjectRow {
  id: string;
  index: number;
  pointer: string;
  name: string;
  entity: string;
  owner: string;
  locked: boolean;
}

/** Each object's owner participant, for an object whose process entry names no owner field (decision 16). */
export function ObjectsGrid({ ctx, scenario }: { ctx: EditorContext; scenario: ScenarioDoc }) {
  const problems = useProblems(
    ctx.id,
    ctx.diagnostics,
    (scenario.objects ?? []).map((o) => o.id),
  );
  const process = useElement(scenario.process).data?.json as unknown as ProcessDoc | undefined;
  const index = useIndex();
  const entities = useMemo(() => new Map((index.data ?? []).filter((r) => r.kind === "entity").map((r) => [r.id, r.name])), [index.data]);
  const withOwnerField = new Set(
    subjectsOf(process)
      .filter((e) => e.owner)
      .map((e) => e.entity),
  );
  const participants = scenario.participants ?? [];
  const [adding, setAdding] = useState(false);
  const rows: ObjectRow[] = (scenario.objects ?? []).map((o, i) => ({
    id: o.id,
    index: i,
    pointer: `/objects/${i}`,
    name: o.name,
    entity: entities.get(o.entity) ?? "?",
    owner: participants.find((p) => p.id === o.owner)?.name ?? "",
    locked: withOwnerField.has(o.entity),
  }));
  const columns: GridColumn<ObjectRow>[] = [
    { key: "name", label: L.name, kind: "readonly", width: "min-w-28", sticky: true, value: (r) => r.name },
    { key: "entity", label: L.entity, kind: "readonly", width: "min-w-28", value: (r) => r.entity },
    {
      key: "owner",
      label: L.owner,
      tooltip: L.ownerLocked,
      kind: "select",
      width: "min-w-24",
      value: (r) => r.owner,
      editValue: (r) => scenario.objects?.[r.index]?.owner ?? "",
      options: () => [{ value: "", label: L.none }, ...participants.map((p) => ({ value: p.id, label: p.name }))],
      locked: (r) => r.locked,
    },
  ];
  const addObject = (name: string, entity: string) => {
    ctx.edit((j) => {
      const sc = j as unknown as ScenarioDoc;
      const id = newId();
      sc.objects = [...(sc.objects ?? []), { id, name, entity }];
      // The first object is the one the process runs for, until another is chosen.
      if (!sc.subject) sc.subject = id;
    });
    ctx.flush();
    setAdding(false);
  };
  const taken = new Set((scenario.objects ?? []).map((o) => o.name.toLowerCase()));
  return (
    <>
      <Grid
        label={`Objects of ${scenario.name}`}
        testid="objects-grid"
        noun="object"
        rows={rows}
        columns={columns}
        rowName={(r) => r.name}
        empty={L.noObjects}
        onAdd={() => setAdding(true)}
        problems={(r) => rowProblems(problems, r.pointer)}
        onCommit={(row, key, value) => {
          if (key !== "owner") return;
          ctx.edit((j) => {
            const o = (j as unknown as ScenarioDoc).objects?.find((x) => x.id === row.id);
            if (!o) return;
            if (value) o.owner = String(value);
            else delete o.owner;
          });
          ctx.flush();
        }}
      />
      {adding ? (
        <AddObjectDialog entities={[...entities].map(([id, name]) => ({ id, name }))} taken={taken} onAdd={addObject} onClose={() => setAdding(false)} />
      ) : null}
    </>
  );
}

/** Add object: a name and the entity it is an instance of. */
function AddObjectDialog({
  entities,
  taken,
  onAdd,
  onClose,
}: {
  entities: { id: string; name: string }[];
  taken: ReadonlySet<string>;
  onAdd: (name: string, entity: string) => void;
  onClose: () => void;
}) {
  const sorted = [...entities].sort((a, b) => a.name.localeCompare(b.name));
  const [entity, setEntity] = useState(sorted[0]?.id ?? "");
  const [name, setName] = useState(() => {
    const base = (sorted[0]?.name ?? "object").replace(/^./, (c) => c.toLowerCase());
    let n = base;
    for (let i = 2; taken.has(n.toLowerCase()); i++) n = `${base}${i}`;
    return n;
  });
  const clash = taken.has(name.trim().toLowerCase());
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={L.addObjectTitle}>
        <form
          className="flex flex-col gap-2"
          data-testid="add-object-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            if (name.trim() && entity && !clash) onAdd(name.trim(), entity);
          }}
        >
          <Field label={L.name} htmlFor="add-object-name">
            <Input id="add-object-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} aria-invalid={clash} />
            {clash ? <p className="text-11 text-danger">{L.objectNameTaken}</p> : null}
          </Field>
          <Field label={L.entity} htmlFor="add-object-entity">
            <Select id="add-object-entity" value={entity} onChange={(e) => setEntity(e.target.value)}>
              {sorted.map((e) => (
                <option key={e.id} value={e.id}>
                  {e.name}
                </option>
              ))}
            </Select>
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || !entity || clash} data-testid="add-object-apply">
              Add
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
