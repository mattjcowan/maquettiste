// The Fires section of the operation screen (processes-operations-design.md sections 7 and 12): the events the operation
// fires, one row each ("Invoice lifecycle › Issue", the payload in small text, its findings), and Add event, a two-step
// picker: a process from the index (no document read), then that process's events from its one document.
import { useEffect, useRef, useState } from "react";
import { Plus, X } from "lucide-react";
import { useElement, useElements, useIndex } from "@/api/queries";
import type { Diagnostic, ElementSummary } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { indexLookup } from "@/model/index";
import { stateIndex, type ProcessDoc, type TransitionDoc } from "@/model/process";
import { groupProcessesForSubject, payloadText } from "@/model/operationLinks";

type Rec = Record<string, unknown>;

interface Fired {
  process: ElementSummary;
  event: string;
  payload: string;
}

interface FiredEvents {
  found: Map<string, Fired>;
  /** Still reading the processes that hold the fired events. */
  loading: boolean;
}

/**
 * Where each fired event lives, without reading the model: an event id is read as it is (a sub-element id answers with the one document
 * that holds it), so an operation reads one process document per distinct fired event and never the other processes.
 */
function useFiredEvents(fires: string[]): FiredEvents {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const holders = useElements([...new Set(fires)]);
  const found = new Map<string, Fired>();
  for (const [id, doc] of holders.byId) {
    const json = doc.json as Rec;
    const process = typeof json.id === "string" ? lookup.byId.get(json.id) : undefined;
    const event = (Array.isArray(json.events) ? (json.events as Rec[]) : []).find((e) => e.id === id);
    if (process && event) found.set(id, { process, event: String(event.name), payload: payloadText(event, lookup.nameOf) });
  }
  return { found, loading: holders.pending };
}

export function FiresSection({
  operation,
  json,
  subject,
  problems,
  edit,
}: {
  operation: string;
  json: Rec;
  subject: string | undefined;
  problems: Diagnostic[];
  edit: (update: (json: Rec) => void) => void;
}) {
  const fires = Array.isArray(json.fires) ? (json.fires as unknown[]).filter((x): x is string => typeof x === "string") : [];
  const { found: fired, loading } = useFiredEvents(fires);
  const [adding, setAdding] = useState(false);
  const label = (id: string) => {
    const f = fired.get(id);
    if (f) return `${f.process.name} › ${f.event}`;
    return loading ? "Reading…" : "Event not found";
  };
  // A newly added row scrolls into view.
  const list = useRef<HTMLUListElement>(null);
  const before = useRef(fires.length);
  useEffect(() => {
    if (fires.length > before.current) list.current?.lastElementChild?.scrollIntoView?.({ block: "nearest" });
    before.current = fires.length;
  }, [fires.length]);
  return (
    <section className="flex flex-col gap-1" aria-label="Fires" data-testid="operation-fires">
      <SectionTitle
        actions={
          <Button size="sm" onClick={() => setAdding(true)} data-testid="add-fired-event">
            <Plus /> Add event
          </Button>
        }
      >
        Fires
      </SectionTitle>
      {fires.length ? (
        <ul ref={list} className="flex flex-col divide-y divide-subtle rounded-control border border-default">
          {fires.map((id, i) => {
            const f = fired.get(id);
            const own = problems.filter((d) => (d.jsonPointer ?? "") === `/fires/${i}` || (d.jsonPointer ?? "").startsWith(`/fires/${i}/`));
            return (
              <li key={`${id}-${i}`} className="flex items-start gap-2 px-2 py-1" data-testid="fired-event">
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="flex items-center gap-2">
                    <span className="truncate text-13">{label(id)}</span>
                  </span>
                  {f?.payload ? <span className="truncate text-11 text-secondary">{f.payload}</span> : null}
                  {own.map((d) => (
                    <span key={`${d.rule}-${d.message}`} className="text-11 text-warning" data-testid="fired-event-finding">
                      {d.rule}: {d.message}
                    </span>
                  ))}
                </span>
                <Button
                  variant="ghost"
                  size="icon-row"
                  label={`Stop firing ${f?.event ?? "this event"}`}
                  onClick={() => edit((j) => void (j.fires = fires.filter((_, k) => k !== i)))}
                >
                  <X />
                </Button>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="text-12 text-secondary">This operation fires no events.</p>
      )}
      {adding ? (
        <AddEventDialog
          operation={operation}
          subject={subject}
          fired={fires}
          onClose={() => setAdding(false)}
          onPick={(event) => {
            edit((j) => void (j.fires = [...fires, event]));
            setAdding(false);
          }}
        />
      ) : null}
    </section>
  );
}

function AddEventDialog({
  operation,
  subject,
  fired,
  onPick,
  onClose,
}: {
  operation: string;
  subject: string | undefined;
  fired: string[];
  onPick: (event: string) => void;
  onClose: () => void;
}) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const [filter, setFilter] = useState("");
  const [process, setProcess] = useState<ElementSummary | null>(null);
  const groups = groupProcessesForSubject(lookup.ofKind("process"), subject, filter);
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={process ? `Events of ${process.name}` : "Add event"}>
        <div className="flex flex-col gap-2" data-testid="add-event-dialog" data-operation={operation}>
          {process ? (
            <EventStep process={process} fired={fired} onBack={() => setProcess(null)} onPick={onPick} />
          ) : (
            <>
              <Input aria-label="Filter processes" placeholder="Filter processes" value={filter} onChange={(e) => setFilter(e.target.value)} autoFocus />
              <div className="flex max-h-72 flex-col gap-2 overflow-auto">
                <ProcessGroup title={`On ${lookup.nameOf(subject ?? "") ?? "its subject"}`} show={!!subject} rows={groups.on} onPick={setProcess} />
                <ProcessGroup title="Other processes" show rows={groups.other} onPick={setProcess} />
                {!groups.on.length && !groups.other.length ? <p className="text-12 text-secondary">No process matches.</p> : null}
              </div>
            </>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}

function ProcessGroup({ title, show, rows, onPick }: { title: string; show: boolean; rows: ElementSummary[]; onPick: (p: ElementSummary) => void }) {
  if (!show || !rows.length) return null;
  return (
    <section aria-label={title} className="flex flex-col">
      <h3 className="text-11 font-semibold text-secondary">{title}</h3>
      {rows.map((p) => (
        <Button key={p.id} variant="ghost" className="justify-start" onClick={() => onPick(p)}>
          {p.name}
        </Button>
      ))}
    </section>
  );
}

function EventStep({ process, fired, onBack, onPick }: { process: ElementSummary; fired: string[]; onBack: () => void; onPick: (event: string) => void }) {
  const doc = useElement(process.id);
  const body = doc.data?.json as Rec | undefined;
  const events = (body?.events as Rec[] | undefined) ?? [];
  const lookup = indexLookup(useIndex().data);
  const states = stateIndex({ states: body?.states as ProcessDoc["states"] });
  const stateName = (id: string) => states.get(id)?.path ?? "?";
  /** "Draft → Issued" for each transition the event triggers. */
  const moves = (event: string) =>
    ((body?.transitions as TransitionDoc[] | undefined) ?? [])
      .filter((t) => t.event === event)
      .map((t) => `${stateName(t.source)} → ${t.targets?.length ? t.targets.map(stateName).join(", ") : stateName(t.source)}`)
      .join("; ");
  return (
    <>
      <div className="flex max-h-72 flex-col overflow-auto">
        {doc.isPending ? <p className="text-12 text-secondary">Loading events</p> : null}
        {!doc.isPending && !events.length ? <p className="text-12 text-secondary">{process.name} has no events.</p> : null}
        {events.map((e) => {
          const id = String(e.id);
          const payload = payloadText(e, lookup.nameOf);
          return (
            <Button key={id} variant="ghost" className="h-auto flex-col items-start py-1" disabled={fired.includes(id)} onClick={() => onPick(id)}>
              <span>{String(e.name)}</span>
              {moves(id) ? <span className="text-11 font-normal text-secondary">{moves(id)}</span> : null}
              {payload ? <span className="text-11 font-normal text-secondary">{payload}</span> : null}
            </Button>
          );
        })}
      </div>
      <div className="flex justify-start">
        <Button size="sm" variant="ghost" onClick={onBack}>
          Back
        </Button>
      </div>
    </>
  );
}
