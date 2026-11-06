// "Taken from the object" in the actor editor (actors-design.md section 9, decisions 8 to 12): the form (the object's owner, a field
// of the object, or the members of a role within a context), a path picked step by step from the lifecycle subjects' to-one ends
// and attributes, and what it means in plain words. Exactly one form; choosing one clears the actor's memberOf.
import { useEffect, useMemo, useState } from "react";
import { useElements, useIndex } from "@/api/queries";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { memberOfChoices, type ActorRow } from "@/model/actors";
import { indexLookup } from "@/model/index";
import { ACTOR_RELATIVE_LABELS as L } from "@/model/labels";
import { canNamePerson, objectFields, relativeForms, relativeMeaning, spaced, type RelativeDoc, type RelativeForm } from "@/model/objectFields";
import type { EditorContext } from "../EditorFrame";
import { useObjectFields } from "./useObjectFields";

type Rec = Record<string, unknown>;
type Ctx = Pick<EditorContext, "id" | "json" | "edit" | "flush">;

/** One step of a path, and the steps after it when the field it picks leads on through a to-one end. */
function PathPicker({
  entities,
  value,
  finish,
  onChange,
  onNames,
  idBase,
  empty,
}: {
  entities: string[];
  value: string[];
  finish: "field" | "end";
  onChange: (next: string[]) => void;
  onNames: (names: Record<string, string>) => void;
  idBase: string;
  empty: string;
}) {
  const { fields } = useObjectFields(entities);
  useEffect(() => onNames(Object.fromEntries(fields.map((f) => [f.id, f.name]))), [fields, onNames]);
  const options = fields.filter((f) => (finish === "end" ? f.kind === "end" && !f.many : canNamePerson(f)));
  const chosen = fields.find((f) => f.id === value[0]);
  const next = chosen?.kind === "end" && !chosen.many && chosen.target ? chosen.target : undefined;
  return (
    <>
      <Select
        id={idBase}
        aria-label="Path step"
        value={value[0] ?? ""}
        onChange={(e) => onChange(e.target.value ? [e.target.value, ...(e.target.value === value[0] ? value.slice(1) : [])] : [])}
        data-testid={idBase}
      >
        <option value="">{empty}</option>
        {value[0] && !options.some((f) => f.id === value[0]) ? <option value={value[0]}>{value[0]}</option> : null}
        {options.map((f) => (
          <option key={f.id} value={f.id}>
            {f.kind === "end" ? `${f.name} →` : f.name}
          </option>
        ))}
      </Select>
      {next && value[0] ? (
        <PathPicker
          entities={[next]}
          value={value.slice(1)}
          finish={finish}
          onChange={(rest) => onChange([value[0], ...rest])}
          onNames={onNames}
          idBase={`${idBase}-${value.length}`}
          empty={L.then}
        />
      ) : null}
    </>
  );
}

/** The entities the actor's processes have as subjects, each once; empty when no process uses it or none has a subject. */
function useUsedSubjects(actor: string): string[] {
  const index = useIndex();
  return useMemo(() => {
    const out: string[] = [];
    for (const r of index.data ?? []) {
      if (r.kind !== "process" || !r.actors?.includes(actor)) continue;
      for (const e of r.subjects ?? (r.subject ? [r.subject] : [])) if (!out.includes(e)) out.push(e);
    }
    return out;
  }, [index.data, actor]);
}

/** Collects the names of the fields a path walks over, entity by entity, for the summary (it renders nothing). */
function PathNames({ entities, path, onNames }: { entities: string[]; path: string[]; onNames: (names: Record<string, string>) => void }) {
  const { fields } = useObjectFields(entities);
  useEffect(() => onNames(Object.fromEntries(fields.map((f) => [f.id, f.name]))), [fields, onNames]);
  const step = fields.find((f) => f.id === path[0]);
  return step?.kind === "end" && step.target && path.length > 1 ? <PathNames entities={[step.target]} path={path.slice(1)} onNames={onNames} /> : null;
}

/**
 * The entity a stored path starts on when no process says (an actor no process uses yet): the first entity that has the field.
 * Loads the documents only when asked to, so a model that no inspector summary needs it for is not read.
 */
function useGuessedSubject(first: string | undefined, enabled: boolean): string | undefined {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const on = enabled && !!first;
  const entityIds = on ? lookup.ofKind("entity").map((e) => e.id) : [];
  const relationIds = on ? lookup.ofKind("relation").map((r) => r.id) : [];
  const stereotypeIds = on ? lookup.ofKind("stereotype").map((s) => s.id) : [];
  const held = useElements([...entityIds, ...relationIds, ...stereotypeIds]);
  if (!on || held.pending) return undefined;
  const doc = (id: string) => held.byId.get(id)?.json as Rec | undefined;
  const sources = {
    doc,
    relations: relationIds.map(doc).filter((d): d is Rec => !!d),
    stereotype: (key: string) => stereotypeIds.map(doc).find((s) => s?.key === key),
    kindOf: (id: string) => lookup.byId.get(id)?.kind,
  };
  return entityIds.find((e) => objectFields(e, sources).some((f) => f.id === first));
}

/** "Taken from the object" read-only, for the inspector: what the actor means, in plain words; nothing for a plain actor. */
export function RelativeSummary({ ctx }: { ctx: Pick<EditorContext, "id" | "json"> }) {
  const rec = ctx.json as Rec;
  const relative = rec.relative && typeof rec.relative === "object" ? (rec.relative as RelativeDoc) : undefined;
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const usedSubjects = useUsedSubjects(ctx.id);
  const [names, setNames] = useState<Record<string, string>>({});
  const onNames = useMemo(
    () => (next: Record<string, string>) => setNames((prev) => (Object.entries(next).every(([k, v]) => prev[k] === v) ? prev : { ...prev, ...next })),
    [],
  );
  const forms = relativeForms(relative);
  const walk = forms[0] === "path" ? (relative?.path ?? []) : forms[0] === "members-of" ? (relative?.within ?? []) : [];
  const guessed = useGuessedSubject(walk[0], usedSubjects.length === 0);
  const used = useMemo(() => (usedSubjects.length ? usedSubjects : guessed ? [guessed] : []), [usedSubjects, guessed]);
  if (!relative) return null;
  const meaning = relativeMeaning(relative, {
    subject: used.length === 1 ? lookup.byId.get(used[0])?.name : undefined,
    field: (id) => names[id] ?? id,
    actor: (id) => lookup.byId.get(id)?.name ?? id,
  });
  return (
    <section className="flex flex-col gap-1" aria-label={L.title} data-testid="actor-relative-summary">
      <SectionTitle>{L.title}</SectionTitle>
      <p className="text-12">{meaning ? L.means(meaning) : L.forms.none}</p>
      {used.length && walk.length ? <PathNames entities={used} path={walk} onNames={onNames} /> : null}
    </section>
  );
}

export function RelativeSection({ ctx }: { ctx: Ctx }) {
  const rec = ctx.json as Rec;
  const relative = rec.relative && typeof rec.relative === "object" ? (rec.relative as RelativeDoc) : undefined;
  const stored: RelativeForm | "none" = relativeForms(relative)[0] ?? "none";
  const [pending, setPending] = useState<RelativeForm | null>(null);
  const form = relative ? stored : (pending ?? "none");
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const used = useUsedSubjects(ctx.id);
  const [chosen, setChosen] = useState("");
  const entities = useMemo(() => (used.length ? used : chosen ? [chosen] : []), [used, chosen]);
  const [names, setNames] = useState<Record<string, string>>({});
  const onNames = useMemo(
    () => (next: Record<string, string>) => setNames((prev) => (Object.entries(next).every(([k, v]) => prev[k] === v) ? prev : { ...prev, ...next })),
    [],
  );
  const rows = (index.data ?? []).filter((r) => r.kind === "actor") as ActorRow[];

  const write = (next: RelativeDoc | undefined) => {
    ctx.edit((j) => {
      const r = j as Rec;
      if (next) {
        r.relative = next;
        delete r.memberOf;
      } else delete r.relative;
    });
    ctx.flush();
  };
  const chooseForm = (next: RelativeForm | "none") => {
    if (next === "none") {
      setPending(null);
      write(undefined);
    } else if (next === "owner") {
      setPending(null);
      write({ owner: true });
    } else {
      setPending(next);
      if (relative) write(undefined);
    }
  };
  const setPath = (path: string[]) => (path.length ? (setPending(null), write({ path })) : (setPending("path"), write(undefined)));
  const setMembers = (membersOf: string | undefined, within: string[]) => {
    if (!membersOf) {
      setPending("members-of");
      write(undefined);
      return;
    }
    setPending(null);
    write({ membersOf, ...(within.length ? { within } : {}) });
  };
  const meaning =
    form === "none" || (form !== "owner" && !relative)
      ? ""
      : relativeMeaning(relative, {
          subject: entities.length === 1 ? lookup.byId.get(entities[0])?.name : undefined,
          field: (id) => names[id] ?? id,
          actor: (id) => lookup.byId.get(id)?.name ?? id,
        });
  const roles = memberOfChoices(rows, ctx.id);

  return (
    <section className="flex flex-col gap-1.5" aria-label={L.title} data-testid="actor-relative">
      <SectionTitle>{L.title}</SectionTitle>
      <Field label={L.form} htmlFor="actor-relative-form">
        <Select id="actor-relative-form" value={form} onChange={(e) => chooseForm(e.target.value as RelativeForm | "none")} data-testid="actor-relative-form">
          {(Object.keys(L.forms) as (keyof typeof L.forms)[]).map((k) => (
            <option key={k} value={k}>
              {L.forms[k]}
            </option>
          ))}
        </Select>
      </Field>
      {form === "owner" ? <p className="text-11 text-secondary">{L.ownerNote}</p> : null}
      {form === "path" || form === "members-of" ? (
        <Field label={L.fieldsOf} htmlFor="actor-relative-entity">
          {used.length ? (
            <span className="text-12" data-testid="actor-relative-subjects">
              {used.map((e) => `a ${spaced(lookup.byId.get(e)?.name ?? e)}`).join(", ")}
            </span>
          ) : (
            <>
              <Select id="actor-relative-entity" value={chosen} onChange={(e) => setChosen(e.target.value)} data-testid="actor-relative-entity">
                <option value="">{L.pickEntity}</option>
                {lookup.ofKind("entity").map((e) => (
                  <option key={e.id} value={e.id}>
                    {e.name}
                  </option>
                ))}
              </Select>
              {!chosen ? <p className="text-11 text-secondary">{L.noSubjects}</p> : null}
            </>
          )}
        </Field>
      ) : null}
      {form === "path" && entities.length ? (
        <div className="flex flex-wrap items-center gap-1" data-testid="actor-relative-path">
          <PathPicker
            entities={entities}
            value={relative?.path ?? []}
            finish="field"
            onChange={setPath}
            onNames={onNames}
            idBase="actor-relative-step"
            empty={L.pick}
          />
        </div>
      ) : null}
      {form === "members-of" ? (
        <div className="flex flex-col gap-1" data-testid="actor-relative-members">
          <Field label={L.role} htmlFor="actor-relative-role">
            <Select
              id="actor-relative-role"
              value={relative?.membersOf ?? ""}
              onChange={(e) => setMembers(e.target.value || undefined, relative?.within ?? [])}
              data-testid="actor-relative-role"
            >
              <option value="">{L.pick}</option>
              {roles.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </Select>
            {!roles.length ? <p className="text-11 text-secondary">{L.noRoles}</p> : null}
          </Field>
          {relative?.membersOf && entities.length ? (
            <Field label={L.context} htmlFor="actor-relative-within">
              <div className="flex flex-wrap items-center gap-1">
                <PathPicker
                  entities={entities}
                  value={relative.within ?? []}
                  finish="end"
                  onChange={(within) => setMembers(relative.membersOf, within)}
                  onNames={onNames}
                  idBase="actor-relative-within"
                  empty={L.itself}
                />
              </div>
            </Field>
          ) : null}
        </div>
      ) : null}
      {meaning ? (
        <p className="text-12" data-testid="actor-relative-meaning">
          {L.means(meaning)}
        </p>
      ) : null}
    </section>
  );
}
