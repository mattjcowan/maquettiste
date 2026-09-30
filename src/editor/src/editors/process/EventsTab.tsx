// The Events tab (phase-3-design.md 6.2): the process's events (name, actors, used by); the selected event's payload
// in the attribute grid below.
import { useState } from "react";
import { SectionTitle } from "@/components/ui/misc";
import { addNamed, idsOfNames, namedRows, removeNamed, type NamedRow } from "@/model/process";
import { Grid, type GridColumn } from "./Grid";
import { ProcessAttributeGrid } from "./AttributeLists";
import { rowProblems, useGridTargets, type ProcessContext } from "./shared";

export function EventsTab({ pc, focus }: { pc: ProcessContext; focus: string | null }) {
  const { process, change, actorNames } = pc;
  const rows = namedRows(process, "events", actorNames);
  const go = useGridTargets(pc.id);
  const [picked, setPicked] = useState<string | null>(null);
  // A navigation to another event wins over the row picked here.
  const [seen, setSeen] = useState(focus);
  if (focus !== seen) {
    setSeen(focus);
    setPicked(null);
  }
  const current = picked ?? focus ?? rows[0]?.id ?? null;
  const event = (process.events ?? []).find((e) => e.id === current);
  const eventIndex = (process.events ?? []).findIndex((e) => e.id === current);
  const actorOptions = pc.actors.map((a) => ({ id: a.id, name: a.name }));
  const columns: GridColumn<NamedRow>[] = [
    { key: "name", label: "Name", kind: "text", width: "min-w-28", value: (r) => r.name },
    {
      key: "actors",
      label: "Actors",
      kind: "multi",
      width: "min-w-40",
      value: (r) => r.actors,
      definition: (r) => go.element((process.events ?? []).find((e) => e.id === r.id)?.actors?.[0]),
      options: () => actorOptions.map((a) => ({ value: a.id, label: a.name })),
    },
    { key: "usedBy", label: "Used by", kind: "readonly", width: "min-w-40", value: (r) => r.usedBy },
  ];
  return (
    <div className="flex flex-col gap-3">
      <Grid
        label={`Events of ${process.name}`}
        testid="events-grid"
        noun="event"
        rows={rows}
        columns={columns}
        rowName={(r) => r.name}
        selected={current}
        onSelect={(r) => setPicked(r.id)}
        problems={(r) => rowProblems(pc.problems, r.pointer)}
        onCommit={(row, key, value) =>
          change((p) => {
            const e = (p.events ?? []).find((x) => x.id === row.id);
            if (!e) return;
            if (key === "name") e.name = String(value).trim() || e.name;
            else if (key === "actors") {
              const ids = idsOfNames(String(value), actorOptions);
              if (ids.length) e.actors = ids;
              else delete e.actors;
            }
          })
        }
        onAdd={(after) => {
          const created: string[] = [];
          change((p) => void created.push(addNamed(p, "events", after?.id)));
          if (created[0]) setPicked(created[0]);
        }}
        onRemove={(row) => change((p) => removeNamed(p, "events", row.id))}
      />
      {event ? (
        <section className="flex flex-col gap-1" aria-label={`Payload of ${event.name}`} data-testid="event-payload">
          <SectionTitle>Payload of {event.name}</SectionTitle>
          <ProcessAttributeGrid
            pc={pc}
            label={`Payload of ${event.name}`}
            owner={event.id}
            pointerBase={`/events/${eventIndex}/payload`}
            at={{
              get: (p) => p.events?.find((e) => e.id === event.id)?.payload,
              set: (p, list) => {
                const e = p.events?.find((x) => x.id === event.id);
                if (!e) return;
                if (list) e.payload = list;
                else delete e.payload;
              },
            }}
          />
        </section>
      ) : null}
    </div>
  );
}
