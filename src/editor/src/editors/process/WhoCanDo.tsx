// "Who can do what" (rights-design.md section 10): one state at a time, actors as rows and verbs as columns. Each cell says
// Allowed, Denied or Depends ("Depends on HR" when another lifecycle may say no), greyed when the lifecycle's default
// decides it. Clicking a cell lists what decides it, each a link. It reads `get_rights`, so it shows the saved lifecycle.
import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useElement, useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { Spinner } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { RIGHT_LABELS as L } from "@/model/labels";
import { indexLookup } from "@/model/index";
import type { Json } from "@/model/process";
import { rightStateOptions } from "@/model/rights";
import { deciders, exceptions, whoEntities, whoStates, whoView, type Decider, type WhoCell } from "@/model/whoCanDo";
import { describeVerb } from "@/model/verbList";
import { useVerbs } from "@/verbs/useVerbs";
import { getRights } from "./api";
import { useGridTargets, type ProcessContext } from "./shared";

const TONE: Record<WhoCell["answer"], string> = {
  allowed: "text-success",
  denied: "text-danger",
  depends: "text-warning",
};

export function WhoCanDo({ pc, named }: { pc: ProcessContext; named: ReadonlyMap<string, Json> }) {
  const { process, actorNames } = pc;
  const go = useGridTargets(pc.id);
  const verbs = useVerbs();
  const hash = useElement(pc.id).data?.hash;
  const lookup = indexLookup(useIndex().data);
  // The Frozen switch asks for the answers of a frozen object (decisions 17 and 18).
  const [frozen, setFrozen] = useState(false);
  const answer = useQuery({ queryKey: ["rights", pc.id, hash, frozen], queryFn: ({ signal }) => getRights(pc.id, signal, frozen), staleTime: 5_000 });
  const rows = answer.data?.rows ?? [];
  const states = rightStateOptions(process);
  const stateName = new Map(states.map((s) => [s.value, s.label]));
  const [picked, setPicked] = useState<string | null>(null);
  const [entityPick, setEntity] = useState("");
  const [cellKey, setCellKey] = useState<string | null>(null);
  const known = whoStates(rows);
  const state = picked && known.includes(picked) ? picked : (known[0] ?? null);
  const entities = whoEntities(rows);
  const entity = entities.includes(entityPick) ? entityPick : null;
  const names = useMemo(
    () => ({
      actor: (id: string | null) => (id ? (actorNames.get(id) ?? "?") : ""),
      verb: (key: string) => describeVerb(key, verbs.own)?.label ?? key,
      lifecycle: (id: string) => lookup.byId.get(id)?.displayName || lookup.byId.get(id)?.name || "?",
      entity: (id: string) => lookup.byId.get(id)?.displayName || lookup.byId.get(id)?.name || "?",
    }),
    [actorNames, verbs.own, lookup],
  );
  if (answer.isPending) return <Spinner label={L.whoLoading} />;
  if (answer.isError)
    return (
      <p className="text-12 text-danger" role="alert">
        {L.whoFailed}
      </p>
    );
  if (!state) return <p className="text-12 text-secondary">{L.whoEmpty}</p>;
  const view = whoView(rows, state, entity, names);
  const chosen = cellKey ? cellKey.split("|") : null;
  const cell = chosen ? view.cell(chosen[0] === "" ? null : chosen[0], chosen[1]) : null;
  const why = cell ? deciders(cell, process, named, (id) => lookup.byId.get(id)?.name ?? null) : [];
  const excepting = chosen ? exceptions(chosen[0] === "" ? null : chosen[0], chosen[1], state, process, named) : [];
  const open = (d: Decider) => {
    if (d.kind === "event") return go.node("events", d.id)?.go();
    if (d.kind === "gate") return go.node("gates", d.id)?.go();
    if (d.kind === "task") return go.node("states", d.id)?.go();
    if (d.kind === "right" && !named.has(d.id) && d.id !== process.id) return go.node("rights", d.id)?.go();
    // A named right or another lifecycle opens in its own editor tab.
    return go.open(d.id)?.go();
  };
  return (
    <div className="flex flex-col gap-2" data-testid="who-can-do-what">
      <div className="flex items-end gap-2">
        <Field label={L.whoState} htmlFor="who-state">
          <Select id="who-state" className="h-6 w-44" data-testid="who-state" value={state} onChange={(e) => (setPicked(e.target.value), setCellKey(null))}>
            {known.map((s) => (
              <option key={s} value={s}>
                {stateName.get(s) ?? "?"}
              </option>
            ))}
          </Select>
        </Field>
        {entities.length ? (
          <Field label={L.whoEntity} htmlFor="who-entity">
            <Select
              id="who-entity"
              className="h-6 w-40"
              data-testid="who-entity"
              value={entity ?? ""}
              onChange={(e) => (setEntity(e.target.value), setCellKey(null))}
            >
              <option value="">{L.whoAllEntities}</option>
              {entities.map((e) => (
                <option key={e} value={e}>
                  {names.entity(e)}
                </option>
              ))}
            </Select>
          </Field>
        ) : null}
        <label className="flex h-6 items-center gap-1 text-12" title={L.whoFrozenTip}>
          <input type="checkbox" checked={frozen} onChange={(e) => (setFrozen(e.target.checked), setCellKey(null))} data-testid="who-frozen" />
          {L.whoFrozenSwitch}
        </label>
        <p className="pb-1 text-11 text-secondary">{L.whoSaved}</p>
      </div>
      <div className="mq-scroll overflow-auto">
        <table className="border-collapse text-12" aria-label={L.whoGrid(stateName.get(state) ?? "")} data-testid="who-grid">
          <thead>
            <tr>
              <th className="sticky left-0 border border-default bg-raised px-2 py-0.5 text-left font-semibold" scope="col">
                {L.columns.who}
              </th>
              {view.verbs.map((v) => (
                <th key={v.key} className="border border-default bg-raised px-2 py-0.5 text-left font-semibold" scope="col">
                  {v.label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {view.actors.map((a) => (
              <tr key={a.id ?? "else"}>
                <th className="sticky left-0 border border-default bg-raised px-2 py-0.5 text-left font-normal" scope="row">
                  {a.label}
                </th>
                {view.verbs.map((v) => {
                  const c = view.cell(a.id, v.key);
                  const key = `${a.id ?? ""}|${v.key}`;
                  return (
                    <td key={v.key} className="border border-default p-0">
                      {c ? (
                        <button
                          type="button"
                          className={cn(
                            "h-5 w-full whitespace-nowrap px-2 text-left hover:bg-accent-subtle",
                            TONE[c.answer],
                            c.grey && "italic !text-secondary",
                            cellKey === key && "bg-accent-subtle",
                          )}
                          aria-pressed={cellKey === key}
                          aria-label={`${a.label}, ${v.label}: ${c.text}${c.grey ? ", by default" : ""}`}
                          data-testid="who-cell"
                          data-answer={c.answer}
                          data-default={c.grey}
                          onClick={() => setCellKey(cellKey === key ? null : key)}
                        >
                          {c.text}
                        </button>
                      ) : null}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {cell ? (
        <div className="flex flex-col gap-0.5 text-12" data-testid="who-deciders">
          <p className="font-semibold" data-testid="who-restated">
            {L.whoRestated(
              view.actors.find((a) => (a.id ?? "") === chosen![0])?.label ?? "",
              view.verbs.find((v) => v.key === chosen![1])?.label ?? "",
              stateName.get(state) ?? "",
              cell.text,
            )}
          </p>
          {excepting.map((n) => (
            <p key={n} className="text-secondary" data-testid="who-excepted">
              {L.whoExcepted(n, view.actors.find((a) => (a.id ?? "") === chosen![0])?.label ?? "")}
            </p>
          ))}
          {cell.grey ? <p className="text-secondary">{L.whoByDefault}</p> : null}
          {!why.length ? <p className="text-secondary">{frozen && cell.answer === "denied" ? L.whoFrozenReason : L.whoNothing}</p> : null}
          {why.length ? (
            <p className="flex flex-wrap items-center gap-1">
              <span className="text-secondary">{cell.by.length ? L.whoDecidedBy : L.whoDependsOn}:</span>
              {why.map((d) => (
                <Button key={d.id} variant="ghost" size="sm" className="h-5 px-1" onClick={() => open(d)} data-testid="who-decider">
                  {d.name} ({L.kinds[d.kind]})
                </Button>
              ))}
            </p>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
