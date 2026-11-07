// The scenario editor's Participants and Objects grids (actors-design.md section 9): who acts in the scenario (name, display
// name, identity, the actor they stand for, what they hold, their record object), and each object's owner participant.
import { useMemo, useState } from "react";
import { Pencil, Plus, X } from "lucide-react";
import { useServices } from "@/app/context";
import { useElement, useElements, useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { EFFECT_LABELS as EL } from "@/model/effectLabels";
import { objectStatesText, parseValuesText, stateChoices, statesOfIds, valuesText, type StateChoiceGroup } from "@/model/effects";
import { indexLookup } from "@/model/index";
import { newId } from "@/lib/ids";
import { PARTICIPANT_LABELS as L, RIGHT_LABELS as RL } from "@/model/labels";
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
import { useObjectFields } from "../actor/useObjectFields";
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

/** The lifecycles that can govern an entity (it, a base of it, or a stereotype it carries is a subject), with their states, but the one the scenario runs. */
export function useLifecycleStates(entities: readonly string[], run: string): Map<string, StateChoiceGroup[]> {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const lifecycles = lookup.ofKind("process").filter((p) => p.use === "lifecycle" && p.id !== run);
  const docs = useElements(entities.length ? lifecycles.map((p) => p.id) : []).byId;
  return useMemo(() => {
    const out = new Map<string, StateChoiceGroup[]>();
    for (const entity of entities) {
      const chain: string[] = [];
      for (let id: string | undefined = entity; id && !chain.includes(id); id = lookup.byId.get(id)?.base) chain.push(id);
      const governing = lifecycles.filter((p) => {
        const subjects = [...(p.subject ? [p.subject] : []), ...(p.subjects ?? []), ...(p.covers ?? [])];
        return chain.some((c) => subjects.includes(c));
      });
      out.set(
        entity,
        stateChoices(
          governing.flatMap((p) => (docs.get(p.id)?.json ? [docs.get(p.id)!.json as unknown as ProcessDoc] : [])),
          run,
        ),
      );
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [docs, index.data, entities.join(","), run]);
}

/** The attribute names of each entity (its own and its bases'), by attribute id. */
export function useAttributeNames(entities: readonly string[]): Map<string, Map<string, string>> {
  const lookup = indexLookup(useIndex().data);
  const chains = new Map<string, string[]>();
  for (const entity of entities) {
    const chain: string[] = [];
    for (let id: string | undefined = entity; id && !chain.includes(id); id = lookup.byId.get(id)?.base) chain.push(id);
    chains.set(entity, chain);
  }
  const held = useElements([...new Set([...chains.values()].flat())]).byId;
  return useMemo(() => {
    const out = new Map<string, Map<string, string>>();
    for (const [entity, chain] of chains) {
      const names = new Map<string, string>();
      for (const id of chain)
        for (const a of (held.get(id)?.json as { attributes?: { id: string; name: string }[] } | undefined)?.attributes ?? []) names.set(a.id, a.name);
      out.set(entity, names);
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [held, entities.join(",")]);
}

interface ObjectRow {
  id: string;
  index: number;
  pointer: string;
  name: string;
  entity: string;
  owner: string;
  states: string;
  values: string;
  frozen: boolean;
  /** The links that start frozen, as "end: object" names. */
  frozenLinks: string;
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
  // An entity covered through a stereotype entry with an owner field has one too (stereotype-sets-design.md decision 6); the
  // process's index row lists the carriers that entry governs.
  const owned = subjectsOf(process).filter((e) => e.owner);
  const withOwnerField = new Set<string>([
    ...owned.flatMap((e) => e.entity ?? []),
    ...(owned.some((e) => e.stereotype !== undefined) ? ((index.data ?? []).find((r) => r.id === scenario.process)?.covers ?? []) : []),
  ]);
  const participants = scenario.participants ?? [];
  const [adding, setAdding] = useState(false);
  const { drafts, store } = useServices();
  const attributeNames = useAttributeNames([...new Set((scenario.objects ?? []).map((o) => o.entity))]);
  const stateGroups = useLifecycleStates([...new Set((scenario.objects ?? []).map((o) => o.entity))], scenario.process);
  const objectFields = useObjectFields([...new Set((scenario.objects ?? []).map((o) => o.entity))]);
  const objectNames = new Map((scenario.objects ?? []).map((o) => [o.id, o.name]));
  const linkLabel = (l: { end: string; object: string }) => `${objectFields.nameOf(l.end)}: ${objectNames.get(l.object) ?? "?"}`;
  const rows: ObjectRow[] = (scenario.objects ?? []).map((o, i) => ({
    id: o.id,
    index: i,
    pointer: `/objects/${i}`,
    name: o.name,
    entity: entities.get(o.entity) ?? "?",
    owner: participants.find((p) => p.id === o.owner)?.name ?? "",
    states: objectStatesText(o.states, stateGroups.get(o.entity) ?? []),
    values: valuesText(o.values, attributeNames.get(o.entity) ?? new Map()),
    frozen: o.frozen === true,
    frozenLinks: (o.links ?? [])
      .filter((l) => l.frozen)
      .map((l) => linkLabel(l))
      .join(", "),
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
    {
      key: "states",
      label: EL.statesColumn,
      tooltip: EL.statesHint,
      kind: "pick",
      width: "min-w-40",
      value: (r) => r.states,
      editValue: (r) => (scenario.objects?.[r.index]?.states ?? []).join(","),
      options: (r) =>
        (stateGroups.get(scenario.objects?.[r.index]?.entity ?? "") ?? []).flatMap((g) =>
          g.states.map((x) => ({
            value: x.id,
            label: x.label.startsWith(`${g.lifecycle}.`) ? x.label.slice(g.lifecycle.length + 1) : x.label,
            group: g.lifecycle,
          })),
        ),
    },
    {
      key: "frozen",
      label: RL.frozenColumn,
      tooltip: RL.frozenColumnTip,
      kind: "toggle",
      width: "w-14",
      value: (r) => (r.frozen ? "yes" : ""),
      on: (r) => r.frozen,
      locked: (r) => r.id !== scenario.subject,
    },
    {
      key: "frozenLinks",
      label: RL.frozenLinksColumn,
      tooltip: RL.frozenLinksTip,
      kind: "pick",
      width: "min-w-28",
      value: (r) => r.frozenLinks,
      editValue: (r) =>
        (scenario.objects?.[r.index]?.links ?? [])
          .filter((l) => l.frozen)
          .map((l) => `${l.end}|${l.object}`)
          .join(","),
      options: (r) => (scenario.objects?.[r.index]?.links ?? []).map((l) => ({ value: `${l.end}|${l.object}`, label: linkLabel(l) })),
      locked: (r) => r.id !== scenario.subject || !(scenario.objects?.[r.index]?.links ?? []).length,
    },
    {
      key: "values",
      label: EL.valuesColumn,
      tooltip: EL.valuesHint,
      kind: "text",
      width: "min-w-48",
      mono: true,
      value: (r) => r.values,
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
    // The dialog closes once the change is accepted; a refusal keeps it open with what was typed.
    void drafts.flushSaved(ctx.id).then((saved) => {
      if (saved) setAdding(false);
      else store.getState().notify(EL.refused, "error");
    });
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
          if (key !== "owner" && key !== "states" && key !== "values" && key !== "frozen" && key !== "frozenLinks") return;
          if (key === "values") {
            const entity = scenario.objects?.find((x) => x.id === row.id)?.entity ?? "";
            const parsed = parseValuesText(String(value), attributeNames.get(entity) ?? new Map());
            // A name the entity does not have is said, not dropped: nothing is saved until the text is right.
            if (parsed.unknown.length) {
              store.getState().notify(EL.unknownAttributes(row.name, parsed.unknown), "error");
              return;
            }
          }
          ctx.edit((j) => {
            const o = (j as unknown as ScenarioDoc).objects?.find((x) => x.id === row.id);
            if (!o) return;
            if (key === "values") {
              const parsed = parseValuesText(String(value), attributeNames.get(o.entity) ?? new Map());
              if (Object.keys(parsed.values).length) o.values = parsed.values;
              else delete o.values;
            } else if (key === "frozen") {
              if (value === true) o.frozen = true;
              else delete o.frozen;
            } else if (key === "frozenLinks") {
              const chosen = new Set(String(value).split(",").filter(Boolean));
              for (const l of o.links ?? []) {
                if (chosen.has(`${l.end}|${l.object}`)) l.frozen = true;
                else delete l.frozen;
              }
            } else if (key === "states") {
              const ids = statesOfIds(String(value), stateGroups.get(o.entity) ?? []);
              if (ids.length) o.states = ids;
              else delete o.states;
            } else if (value) o.owner = String(value);
            else delete o.owner;
          });
          ctx.flush();
        }}
      />
      {adding ? (
        <AddObjectDialog
          entities={[...entities].map(([id, name]) => ({ id, name }))}
          preferred={subjectsOf(process).find((e) => e.entity && entities.has(e.entity))?.entity}
          taken={taken}
          onAdd={addObject}
          onClose={() => setAdding(false)}
        />
      ) : null}
    </>
  );
}

/** Add object: a name and the entity it is an instance of. */
function AddObjectDialog({
  entities,
  preferred,
  taken,
  onAdd,
  onClose,
}: {
  entities: { id: string; name: string }[];
  /** The entity to start on: the process's subject. */
  preferred?: string;
  taken: ReadonlySet<string>;
  onAdd: (name: string, entity: string) => void;
  onClose: () => void;
}) {
  const sorted = [...entities].sort((a, b) => a.name.localeCompare(b.name));
  const start = sorted.find((e) => e.id === preferred) ?? sorted[0];
  const [entity, setEntity] = useState(start?.id ?? "");
  const [name, setName] = useState(() => {
    const base = (start?.name ?? "object").replace(/^./, (c) => c.toLowerCase());
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
            <Input
              id="add-object-name"
              autoFocus
              onFocus={(e) => e.currentTarget.select()}
              value={name}
              onChange={(e) => setName(e.target.value)}
              aria-invalid={clash}
            />
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
