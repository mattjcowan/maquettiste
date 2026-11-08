// What happens when (trigger-points-design.md section 11), the second side of the Trigger points tab: a verb, and optionally a state and
// a type, and the steps in order, each a link. It shows exactly what `get_trigger_sequence` answers, for the saved lifecycle; the editor
// never works a sequence out. Other lifecycles' points carry their lifecycle's name.
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useElement, useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { Spinner } from "@/components/ui/misc";
import { indexLookup } from "@/model/index";
import { TRIGGER_LABELS as L } from "@/model/labels";
import { subjectsOf, transitionLabel } from "@/model/process";
import { rightStateOptions } from "@/model/rights";
import { sequenceRows } from "@/model/triggerPoints";
import { describeVerb, verbGroups } from "@/model/verbList";
import { useVerbs } from "@/verbs/useVerbs";
import { useObjectFields } from "../actor/useObjectFields";
import { getTriggerSequence } from "./api";
import { useSetContext } from "./SetsEditor";
import { useGridTargets, type ProcessContext } from "./shared";

export function WhatHappensWhen({ pc }: { pc: ProcessContext }) {
  const { process } = pc;
  const verbs = useVerbs();
  const go = useGridTargets(pc.id);
  const lookup = indexLookup(useIndex().data);
  const hash = useElement(pc.id).data?.hash;
  const options = verbGroups("trigger-point", verbs.own, verbs.hidden).flatMap((g) => g.options.map((o) => ({ value: o.key, label: o.label, group: g.label })));
  const [verb, setVerb] = useState("");
  const [state, setState] = useState("");
  const [entity, setEntity] = useState("");
  const states = rightStateOptions(process);
  const entities = subjectsOf(process).flatMap((s) => (s.entity ? [s.entity] : []));
  const stateName = new Map(states.map((s) => [s.value, s.label]));
  const transitionName = new Map((process.transitions ?? []).map((t) => [t.id, transitionLabel(process, t)]));
  // Attributes and links are not in the model index: their names come from the subjects, as the grid's Where column reads them.
  const setContext = useSetContext(process);
  const objectFields = useObjectFields(entities);
  const answer = useQuery({
    queryKey: ["trigger-sequence", pc.id, hash, verb, state, entity],
    queryFn: ({ signal }) => getTriggerSequence(pc.id, { verb, state, entity }, signal),
    enabled: verb !== "",
    staleTime: 5_000,
  });
  const lifecycleName = (id: string) => lookup.byId.get(id)?.displayName || lookup.byId.get(id)?.name || "?";
  const pointName = (id: string) => (process.triggerPoints ?? []).find((t) => t.id === id)?.name ?? lifecycleName(id);
  const rows = answer.data
    ? sequenceRows(answer.data, {
        point: pointName,
        lifecycle: lifecycleName,
        where: (key, ids) =>
          ids
            .map((id) =>
              key === "states" || key === "targets"
                ? (stateName.get(id) ?? "?")
                : key === "transitions"
                  ? (transitionName.get(id) ?? "?")
                  : key === "attributes" || key === "ends"
                    ? (setContext.names.name(id) ?? objectFields.nameOf(id))
                    : (lookup.byId.get(id)?.name ?? "?"),
            )
            .join(", "),
      })
    : [];
  return (
    <div className="flex flex-col gap-2" data-testid="what-happens-when">
      <div className="flex items-end gap-2">
        <Field label={L.happens.verb} htmlFor="happens-verb">
          <Select
            id="happens-verb"
            className="h-6 w-44"
            data-testid="happens-verb"
            title={L.happens.verbTip}
            value={verb}
            onChange={(e) => setVerb(e.target.value)}
          >
            <option value="">…</option>
            {options.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field label={L.happens.state} htmlFor="happens-state">
          <Select id="happens-state" className="h-6 w-40" data-testid="happens-state" value={state} onChange={(e) => setState(e.target.value)}>
            <option value="">{L.happens.stateAny}</option>
            {states.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </Select>
        </Field>
        {entities.length ? (
          <Field label={L.happens.entity} htmlFor="happens-entity">
            <Select id="happens-entity" className="h-6 w-40" data-testid="happens-entity" value={entity} onChange={(e) => setEntity(e.target.value)}>
              <option value="">{L.happens.entityAny}</option>
              {entities.map((e) => (
                <option key={e} value={e}>
                  {lookup.byId.get(e)?.name ?? "?"}
                </option>
              ))}
            </Select>
          </Field>
        ) : null}
        <p className="pb-1 text-11 text-secondary">{L.happens.saved}</p>
      </div>
      {!verb ? <p className="text-12 text-secondary">{L.happens.empty}</p> : null}
      {verb && answer.isPending ? <Spinner label={L.happens.loading} /> : null}
      {verb && answer.isError ? (
        <p className="text-12 text-danger" role="alert">
          {L.happens.failed}
        </p>
      ) : null}
      {verb && answer.data ? (
        <ol className="flex flex-col" aria-label={L.happens.list(describeVerb(verb, verbs.own)?.label ?? verb)} data-testid="sequence-list">
          {rows.map((r, i) => (
            <li
              key={r.key}
              className="flex h-[var(--mq-row-h)] items-center gap-2 border-b border-default px-1 text-12"
              data-testid="sequence-row"
              data-order={i + 1}
            >
              <span className="w-5 shrink-0 text-right text-secondary">{i + 1}</span>
              <span className="w-80 shrink-0 text-secondary">{r.step}</span>
              {r.point ? (
                <>
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-5 px-1"
                    title={L.happens.open(r.point.name)}
                    onClick={() => (r.named ? go.open(r.point!.id) : r.other ? go.open(r.point!.lifecycle!) : go.node("triggerPoints", r.point!.id))?.go()}
                    data-testid="sequence-link"
                  >
                    {r.point.name}
                  </Button>
                  <span className="shrink-0 text-secondary">{r.kind}</span>
                  {r.named ? (
                    <span className="shrink-0 rounded-control border border-default bg-app px-1 text-11" data-testid="sequence-named">
                      {L.happens.namedPoint}
                    </span>
                  ) : null}
                  {r.other ? (
                    <span className="shrink-0 rounded-control border border-default bg-app px-1 text-11" data-testid="sequence-lifecycle">
                      {L.happens.otherLifecycle(r.other)}
                    </span>
                  ) : null}
                  {r.where ? (
                    <span className="min-w-0 truncate text-secondary" title={r.where}>
                      {r.where}
                    </span>
                  ) : null}
                  {r.conditional ? <span className="shrink-0 text-11 text-secondary">{L.happens.conditional}</span> : null}
                  {r.outside ? <span className="shrink-0 text-11 text-warning">{L.happens.outside}</span> : null}
                </>
              ) : (
                <span className="text-secondary">{L.happens.builtIn}</span>
              )}
            </li>
          ))}
        </ol>
      ) : null}
    </div>
  );
}
