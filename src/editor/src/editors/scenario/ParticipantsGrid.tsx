// The scenario editor's Participants and Objects grids (actors-design.md section 9): who acts in the scenario (name, display
// name, identity, the actor they stand for, what they hold, their record object), and each object's owner participant.
import { useMemo } from "react";
import { useElement, useIndex } from "@/api/queries";
import { useServices } from "@/app/context";
import { newId } from "@/lib/ids";
import { PARTICIPANT_LABELS as L } from "@/model/labels";
import {
  forgetParticipant,
  holdable,
  identityOf,
  newParticipantName,
  parseHolds,
  participantRows,
  standsFor,
  type ActorChoice,
  type ParticipantRow,
} from "@/model/participants";
import { subjectsOf, type ProcessDoc, type ScenarioDoc } from "@/model/process";
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
  const { store } = useServices();
  const choices = useActorChoices();
  const problems = useProblems(ctx.id, ctx.diagnostics, partIds(scenario));
  const actorNames = new Map(choices.map((a) => [a.id, a.name]));
  const objects = scenario.objects ?? [];
  const rows = participantRows(scenario, actorNames);
  const none: GridOption = { value: "", label: L.none };

  const columns: GridColumn<ParticipantRow>[] = [
    { key: "name", label: L.name, kind: "text", width: "min-w-28", sticky: true, value: (r) => r.name },
    { key: "displayName", label: L.displayName, kind: "text", width: "min-w-28", value: (r) => r.displayName },
    { key: "identity", label: L.identity, kind: "text", width: "min-w-24", value: (r) => r.identity },
    {
      key: "actor",
      label: L.actor,
      kind: "select",
      width: "min-w-28",
      value: (r) => r.actor,
      editValue: (r) => scenario.participants?.[r.index]?.actor ?? "",
      options: () => [none, ...standsFor(choices).map((a) => ({ value: a.id, label: a.type === "system" ? L.automatic : a.name }))],
    },
    { key: "holds", label: L.holds, tooltip: L.holdsHint, kind: "text", width: "min-w-40", value: (r) => r.holds },
    {
      key: "object",
      label: L.object,
      kind: "select",
      width: "min-w-28",
      value: (r) => r.object,
      editValue: (r) => scenario.participants?.[r.index]?.object ?? "",
      options: () => [none, ...objects.map((o) => ({ value: o.id, label: o.name }))],
    },
  ];

  const commit = (row: ParticipantRow, key: string, value: string | boolean) => {
    const v = String(value);
    if (key === "holds") {
      const parsed = parseHolds(v, holdable(choices), objects, scenario.participants?.[row.index]?.holds, newId);
      if (parsed.error) {
        store.getState().notify(`${row.name}: ${parsed.error}`, "error");
        return;
      }
      ctx.edit((j) => {
        const p = (j as unknown as ScenarioDoc).participants?.find((x) => x.id === row.id);
        if (!p) return;
        if (parsed.holds.length) p.holds = parsed.holds;
        else delete p.holds;
      });
      ctx.flush();
      return;
    }
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

  return (
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
      width: "min-w-28",
      value: (r) => r.owner,
      editValue: (r) => scenario.objects?.[r.index]?.owner ?? "",
      options: () => [{ value: "", label: L.none }, ...participants.map((p) => ({ value: p.id, label: p.name }))],
      locked: (r) => r.locked,
    },
  ];
  return (
    <Grid
      label={`Objects of ${scenario.name}`}
      testid="objects-grid"
      noun="object"
      rows={rows}
      columns={columns}
      rowName={(r) => r.name}
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
  );
}
