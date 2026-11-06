// The process editor's top controls (phase-3-design.md 6.2), also the inspector's Process section: name, domain, use,
// subject, the bound attribute with its MQ9203 badge and Sync enum (a dry run shown in a small confirm, then applied
// as one undo step), and the stereotype, tag and category chips.
import { PROCESS_LABELS } from "@/model/labels";
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { RefreshCcw } from "lucide-react";
import { keys, useElement, useElements, useIndex } from "@/api/queries";
import type { AttributeDoc, ElementSummary } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Select } from "@/components/ui/input";
import { Badge } from "@/components/ui/misc";
import { useServices } from "@/app/context";
import { bindableAttributes, classifyAttribute, codeKindOf } from "@/model/binding";
import { indexLookup } from "@/model/index";
import { stereotypeAppliesToEntities, subjectsOf, syncPlanLines } from "@/model/process";
import { MarkChips, NameAndDomain, domIdOf, type EditorContext } from "../EditorFrame";
import {
  applySyncEnum,
  demoteLifecycle,
  replaceSoleSubject,
  setLifecycle,
  setSubjectOwner,
  stereotypeEnumPlan,
  syncEnum,
  syncStereotypeEntry,
  type SyncEnumResult,
} from "./api";
import { useObjectFields } from "../actor/useObjectFields";
import { canBeOwner, stereotypeFields } from "@/model/objectFields";
import { entryIdsOf, useProblems } from "./shared";

type Rec = Record<string, unknown>;

/**
 * The subject's attributes a lifecycle may bind (own, inherited or from a stereotype; single-valued, typed by a
 * non-flags enum, a reference type, or a plain status; lifecycle-design.md decision 11), the bound enum's id and its
 * member names (null until loaded). `enumId` and `members` stay enum-only.
 */
export function useBinding(subject: string | undefined, bound: string | undefined, stereotypeKey?: string) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const chain: string[] = [];
  for (let id = subject; id && !chain.includes(id); id = lookup.byId.get(id)?.base) chain.push(id);
  const hasStereotypes = chain.some((id) => (lookup.byId.get(id)?.stereotypes ?? []).length > 0);
  const stereotypeIds = hasStereotypes || stereotypeKey ? lookup.ofKind("stereotype").map((s) => s.id) : [];
  const held = useElements([...chain, ...stereotypeIds]).byId;
  const json = (id: string) => held.get(id)?.json as Rec | undefined;
  const stereotype = (key: string) => stereotypeIds.map(json).find((s) => s?.key === key);
  // A stereotype entry binds one of the stereotype's own attributes (stereotype-sets-design.md decision 6).
  const candidates = stereotypeKey
    ? (((stereotype(stereotypeKey)?.attributes as Rec[] | undefined) ?? []) as Rec[])
    : bindableAttributes(json(subject ?? ""), json, stereotype);
  // Enums and scalar types are read for what the index does not say (flags, the base); the others by their kind.
  const typeIds = [
    ...new Set(
      candidates.flatMap((a) => {
        const ref = typeof a.type === "object" && a.type ? (a.type as { ref?: string }).ref : undefined;
        const kind = ref ? lookup.byId.get(ref)?.kind : undefined;
        return ref && (kind === "enum" || kind === "scalar-type" || kind === "reference-type") ? [ref] : [];
      }),
    ),
  ];
  const types = useElements(typeIds).byId;
  const typeDoc = (id: string): Rec | undefined => {
    const loaded = types.get(id)?.json as Rec | undefined;
    if (loaded) return loaded;
    const row = lookup.byId.get(id);
    return row?.kind === "reference-type" ? ({ kind: row.kind, name: row.name } as Rec) : undefined;
  };
  const attributes = candidates.filter((a) => {
    const type = classifyAttribute(a, typeDoc);
    return type.kind === "enum" || type.kind === "reference" || type.kind === "plain";
  }) as unknown as AttributeDoc[];
  const attribute = attributes.find((a) => a.id === bound);
  const bindType = attribute ? classifyAttribute(attribute as unknown as Rec, typeDoc) : undefined;
  const enumId = bindType?.kind === "enum" ? (bindType.typeId ?? null) : null;
  const enumDoc = useElement(enumId);
  const members = enumDoc.data ? (((enumDoc.data.json as { members?: { name: string }[] }).members ?? []).map((m) => m.name) as string[]) : null;
  const codeKind = bindType && attribute ? codeKindOf(bindType, attribute as unknown as Rec, typeDoc) : undefined;
  return { attributes, enumId, members, codeKind };
}

/** A stereotype that can be a subject: its key, name and document. */
export interface StereotypeChoice {
  id: string;
  key: string;
  name: string;
  json: Rec;
  /** Whether it applies to entities (MQ9211 refuses one that does not). */
  forEntities: boolean;
}

/** Every stereotype of the model, with its document (read once each; a project has few). */
export function useStereotypes(): StereotypeChoice[] {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const rows = lookup.ofKind("stereotype");
  const docs = useElements(rows.map((r) => r.id)).byId;
  return rows.flatMap((r) => {
    const json = docs.get(r.id)?.json as Rec | undefined;
    return json && typeof json.key === "string"
      ? [{ id: r.id, key: json.key, name: r.displayName || r.name || json.key, json, forEntities: stereotypeAppliesToEntities(json) }]
      : [];
  });
}

export interface SubjectMembers {
  /** The entity's id, or `stereotype:<key>` for a stereotype entry. */
  entity: string;
  name: string;
  members: string[];
}

/** For a process with several subjects: each enum-bound subject's entity name and bound enum member names. */
export function useSubjectMembers(process: unknown): SubjectMembers[] {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const all = subjectsOf(process as Rec);
  const many = all.length > 1;
  const entities = useElements(many ? all.flatMap((s) => s.entity ?? []) : []).byId;
  const stereotypes = useStereotypes();
  const enumOf = (s: { entity?: string; stereotype?: string; boundAttribute?: string }): string | undefined => {
    const doc = s.stereotype !== undefined ? stereotypes.find((x) => x.key === s.stereotype)?.json : (entities.get(s.entity ?? "")?.json as Rec | undefined);
    const attributes = (doc?.attributes ?? []) as Rec[];
    const ref = (attributes.find((a) => a.id === s.boundAttribute)?.type as { ref?: string } | undefined)?.ref;
    return ref && lookup.byId.get(ref)?.kind === "enum" ? ref : undefined;
  };
  const enums = useElements(many ? all.flatMap((s) => enumOf(s) ?? []) : []).byId;
  if (!many) return [];
  return all.flatMap((s) => {
    const enumId = enumOf(s);
    const doc = enumId ? (enums.get(enumId)?.json as { members?: { name: string }[] } | undefined) : undefined;
    if (!doc) return [];
    const members = (doc.members ?? []).map((m) => m.name);
    return s.stereotype !== undefined
      ? [{ entity: `stereotype:${s.stereotype}`, name: s.stereotype, members }]
      : [{ entity: s.entity!, name: lookup.byId.get(s.entity!)?.name ?? s.entity!, members }];
  });
}

export function ProcessControls({ ctx }: { ctx: EditorContext }) {
  return (
    <div className="flex flex-col gap-2" data-testid="process-controls">
      <NameAndDomain {...ctx}>
        <BindingFields ctx={ctx} dom={domIdOf(ctx.id)} />
      </NameAndDomain>
      <MarkChips {...ctx} />
    </div>
  );
}

/** Use, subject and the bound attribute with its drift badge and Sync enum; `dom` prefixes the field ids (the editor
 * and the inspector can show them at once). */
export function BindingFields({ ctx, dom }: { ctx: Pick<EditorContext, "id" | "json" | "edit" | "flush" | "diagnostics">; dom: string }) {
  const { json, edit, flush, id } = ctx;
  const rec = json as Rec;
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const entities = lookup.ofKind("entity");
  const use = rec.use === "lifecycle" ? "lifecycle" : "orchestration";
  // What the process governs, over both forms (lifecycle-design.md decisions 5 and 10): one subject is editable in the
  // form the file has; two or more are shown read-only (the JSON view edits them).
  const all = subjectsOf(rec);
  const many = all.length > 1;
  const sole = all.length === 1 ? all[0] : undefined;
  const subject = sole?.entity;
  const stereotypeKey = sole?.stereotype;
  const bound = sole?.boundAttribute;
  const listForm = Array.isArray(rec.subjects);
  const { attributes, enumId } = useBinding(subject, bound, stereotypeKey);
  const stereotypes = useStereotypes();
  const chosenStereotype = stereotypeKey ? stereotypes.find((s) => s.key === stereotypeKey) : undefined;
  const scalarDocs = useElements(stereotypeKey ? lookup.ofKind("scalar-type").map((s) => s.id) : []).byId;
  const entityFields = useObjectFields(subject ? [subject] : []).fields;
  // The owner is a field of the entity, or on a stereotype entry one of the stereotype's own attributes (decision 6).
  const ownerFields = (
    stereotypeKey
      ? stereotypeFields(chosenStereotype?.json, {
          doc: (id) => scalarDocs.get(id)?.json as Rec | undefined,
          kindOf: (id) => lookup.byId.get(id)?.kind,
        })
      : entityFields
  ).filter(canBeOwner);
  const [still, setStill] = useState<string | null>(null);
  const problems = useProblems(id, ctx.diagnostics, entryIdsOf(rec));
  const drift = problems.find((d) => d.rule === "MQ9203");
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const name = typeof rec.name === "string" ? rec.name : id;
  // A lifecycle binds both sides: Use and Subject changes that make or keep it a lifecycle, or leave one, go through
  // the set-lifecycle operation (the process, the entity and any previous partner in one batch, one undo step).
  const bind = (entity: string, target: string | undefined, label: string) =>
    setLifecycle(qc, store, drafts, { process: id, entity, target, label }).then((refused) => {
      if (refused) store.getState().notify(`${label}: ${refused}`, "error");
    });
  const setUse = (next: string) => {
    if (next === use) return;
    if (next === "lifecycle") {
      if (subject) bind(subject, id, `Make ${name} a lifecycle`);
      return;
    }
    // Demoting keeps the subjects and clears their bindings, in either form (decision 10); an entity naming it lets go.
    void demoteLifecycle(qc, store, drafts, { process: id, label: `Make ${name} an orchestration` }).then((refused) => {
      if (refused) store.getState().notify(`Make ${name} an orchestration: ${refused}`, "error");
    });
  };
  // After the entity leaves this process, say when another lifecycle still covers it through a stereotype entry.
  const reportCoverage = async (left: string | undefined) => {
    if (!left) return;
    await qc.refetchQueries({ queryKey: keys.index });
    const rows = qc.getQueryData<ElementSummary[]>(keys.index) ?? [];
    // Only this process's stereotype entries count, and only those that cover the entity: the entity's own stereotypes
    // (or a base's) when they name one; a default from a package carries no mark in the index, so then all of its entries.
    const mine = rows.find((r) => r.id === id);
    const names = new Set<string>();
    if (mine?.covers?.includes(left)) {
      const byId = new Map(rows.map((r) => [r.id, r]));
      const own = new Set<string>();
      for (let e = byId.get(left), hops = 0; e && hops < 32; e = e.base ? byId.get(e.base) : undefined, hops++) for (const k of e.stereotypes ?? []) own.add(k);
      const entries = mine.subjectStereotypes ?? [];
      const matching = entries.filter((s) => own.has(byId.get(s)?.name ?? ""));
      for (const s of matching.length ? matching : entries) names.add(byId.get(s)?.displayName || byId.get(s)?.name || s);
    }
    setStill(names.size ? `Still covered through stereotype ${[...names].join(", ")}` : null);
  };
  const setSubject = (choice: string | undefined) => {
    setStill(null);
    const picked = choice?.startsWith("stereotype:") ? stereotypes.find((s) => s.key === choice.slice("stereotype:".length)) : undefined;
    const next = picked ? undefined : choice;
    if (picked) {
      void replaceSoleSubject(qc, store, drafts, {
        process: id,
        stereotype: picked.key,
        ownAttributes: ((picked.json.attributes as Rec[] | undefined) ?? []).flatMap((a) => (typeof a.id === "string" ? [a.id] : [])),
        label: `Set the subject of ${name}`,
      }).then((refused) => {
        if (refused) store.getState().notify(`Set the subject of ${name}: ${refused}`, "error");
      });
      return;
    }
    const left = subject && next !== subject ? subject : undefined;
    const after = () => void reportCoverage(left);
    if (!listForm && !(all.length === 0 && next)) {
      if (use === "lifecycle" && next) void bind(next, id, `Set the subject of ${name}`).then(after);
      else {
        set("subject", next);
        after();
      }
      return;
    }
    // No subject yet: it gets `subjects`, from set-lifecycle for a lifecycle (decision 7). With a list: edit its one entry.
    if (all.length === 0 && use === "lifecycle" && next) void bind(next, id, `Set the subject of ${name}`).then(after);
    else if (use === "lifecycle" && next && Array.isArray(rec.subjects) && rec.subjects.length === 1)
      void replaceSoleSubject(qc, store, drafts, { process: id, entity: next, label: `Set the subject of ${name}` }).then((refused) => {
        if (refused) store.getState().notify(`Set the subject of ${name}: ${refused}`, "error");
        after();
      });
    else {
      edit((j) => {
        const r = j as Rec;
        if (!next) delete r.subjects;
        else if (Array.isArray(r.subjects) && r.subjects.length === 1) r.subjects = [{ id: (r.subjects[0] as Rec).id, entity: next }];
        else r.subjects = [{ entity: next }];
      });
      flush();
      after();
    }
  };
  const set = (key: string, value: string | undefined) => {
    edit((j) => {
      const r = j as Rec;
      if (value) r[key] = value;
      else delete r[key];
      if (key === "use" && value !== "lifecycle") {
        delete r.boundAttribute;
        if (Array.isArray(r.subjects)) r.subjects = (r.subjects as Rec[]).map(({ boundAttribute: _unbound, ...entry }) => entry);
      }
      if (key === "subject") delete r.boundAttribute;
    });
    flush();
  };
  const setBound = (value: string | undefined) => {
    if (!listForm) return set("boundAttribute", value);
    edit((j) => {
      const entries = (j as Rec).subjects as Rec[] | undefined;
      if (!entries?.[0]) return;
      if (value) entries[0].boundAttribute = value;
      else delete entries[0].boundAttribute;
    });
    flush();
  };
  return (
    <>
      <Field label="Use" htmlFor={`${dom}-use`}>
        <span title={many ? "Several subjects: change them in the JSON view" : undefined} className="block">
          <Select
            id={`${dom}-use`}
            value={use}
            title={PROCESS_LABELS.useMeaning[use as "lifecycle" | "orchestration"] ?? PROCESS_LABELS.useMeaning.orchestration}
            disabled={many}
            onChange={(e) => setUse(e.target.value)}
          >
            <option value="orchestration">Orchestration</option>
            <option value="lifecycle" disabled={all.length === 0}>
              {all.length ? "Lifecycle" : "Lifecycle (choose a subject first)"}
            </option>
          </Select>
        </span>
      </Field>
      <Field label="Subject" htmlFor={`${dom}-subject`}>
        {many ? (
          <SubjectList subjects={all} />
        ) : (
          <Select
            id={`${dom}-subject`}
            value={stereotypeKey ? `stereotype:${stereotypeKey}` : (subject ?? "")}
            onChange={(e) => setSubject(e.target.value || undefined)}
          >
            {use === "lifecycle" && (subject || stereotypeKey) ? null : <option value="">{use === "lifecycle" ? "(choose an entity)" : "(none)"}</option>}
            {entities.map((e) => (
              <option key={e.id} value={e.id}>
                {e.name}
              </option>
            ))}
            {stereotypes.some((s) => s.forEntities || s.key === stereotypeKey) ? (
              <optgroup label="Stereotypes">
                {stereotypes
                  .filter((s) => s.forEntities || s.key === stereotypeKey)
                  .map((s) => (
                    <option key={s.id} value={`stereotype:${s.key}`}>
                      {s.name}
                    </option>
                  ))}
              </optgroup>
            ) : null}
          </Select>
        )}
        {still ? (
          <p className="mt-0.5 text-11 text-secondary" data-testid="still-covered">
            {still}
          </p>
        ) : null}
      </Field>
      {sole && !many ? (
        <Field label="Owner" htmlFor={`${dom}-owner`}>
          <Select
            id={`${dom}-owner`}
            value={sole.owner ?? ""}
            title="Where the object's owner is held. Without one, the team's system holds it."
            onChange={(e) =>
              void setSubjectOwner(qc, store, drafts, { process: id, owner: e.target.value || undefined, label: `Set the owner of ${name}` }).then(
                (refused) => {
                  if (refused) store.getState().notify(`Set the owner of ${name}: ${refused}`, "error");
                },
              )
            }
          >
            <option value="">(none)</option>
            {sole.owner && !ownerFields.some((f) => f.id === sole.owner) ? <option value={sole.owner}>{sole.owner}</option> : null}
            {ownerFields.map((f) => (
              <option key={f.id} value={f.id}>
                {f.kind === "end" ? `${f.name} (relation)` : f.name}
              </option>
            ))}
          </Select>
        </Field>
      ) : null}
      {use === "lifecycle" && !many ? (
        <Field label="Bound attribute" htmlFor={`${dom}-bound`}>
          <div className="flex items-center gap-1">
            <Select id={`${dom}-bound`} className="min-w-0 flex-1" value={bound ?? ""} onChange={(e) => setBound(e.target.value || undefined)}>
              <option value="">(none)</option>
              {attributes.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </Select>
            {drift ? (
              <Badge tone="danger" title={drift.message} data-testid="enum-drift">
                MQ9203
              </Badge>
            ) : null}
            <SyncEnumButton process={id} enumId={enumId} entity={listForm ? subject : undefined} stereotype={stereotypeKey} disabled={!bound || !enumId} />
          </div>
        </Field>
      ) : null}
    </>
  );
}

/** The subjects of a process with several, read-only: the entity and its bound attribute (or "no attribute"). */
function SubjectList({ subjects }: { subjects: ReturnType<typeof subjectsOf> }) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const docs = useElements(subjects.flatMap((s) => s.entity ?? [])).byId;
  const stereotypes = useStereotypes();
  return (
    <ul className="flex flex-col gap-0.5 text-12" data-testid="process-subjects" title="Several subjects: change them in the JSON view">
      {subjects.map((s, n) => {
        const doc = s.stereotype !== undefined ? stereotypes.find((x) => x.key === s.stereotype)?.json : (docs.get(s.entity ?? "")?.json as Rec | undefined);
        const attributes = (doc?.attributes ?? []) as Rec[];
        const attribute = s.boundAttribute ? String(attributes.find((a) => a.id === s.boundAttribute)?.name ?? s.boundAttribute) : null;
        return (
          <li key={s.id || n} className="flex items-baseline justify-between gap-2 rounded-control border border-default px-1.5 py-0.5">
            <span className="truncate">{s.stereotype !== undefined ? `${s.stereotype} (stereotype)` : (lookup.byId.get(s.entity!)?.name ?? s.entity)}</span>
            <span className={attribute ? "truncate font-mono text-11" : "text-11 text-secondary"}>{attribute ?? "no attribute"}</span>
            {s.owner ? (
              <span className="text-11 text-secondary" data-testid="subject-owner">
                owner {String(attributes.find((a) => a.id === s.owner)?.name ?? "(relation)")}
              </span>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}

/** Sync enum: the dry run's added, removed, reordered and refused members in a confirm, then the apply. */
export function SyncEnumButton({
  process,
  enumId,
  entity,
  stereotype,
  disabled,
}: {
  process: string;
  enumId: string | null;
  entity?: string;
  /** The key of a stereotype entry: it syncs through the batch, since the endpoint is entity-only. */
  stereotype?: string;
  disabled: boolean;
}) {
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const [plan, setPlan] = useState<SyncEnumResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const open = async () => {
    setBusy(true);
    setError(null);
    try {
      await drafts.flush(process);
      setPlan(stereotype && enumId ? await stereotypeEnumPlan(process, enumId) : await syncEnum(process, true, entity));
    } catch (e) {
      store.getState().notify(`Sync enum: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setBusy(false);
    }
  };
  const apply = async () => {
    if (!enumId) return;
    setBusy(true);
    try {
      if (stereotype) {
        const refused = await syncStereotypeEntry(qc, store, { process, stereotype, op: "sync-enum", enumId, label: "Sync enum" });
        if (refused) setError(refused);
        else {
          setPlan(null);
          store.getState().notify("The enum now matches the states.");
        }
        return;
      }
      const result = await applySyncEnum(qc, store, process, enumId, entity);
      if (result.applied) {
        setPlan(null);
        store.getState().notify("The enum now matches the states.");
      } else setError(result.diagnostics[0]?.message ?? "The sync was refused.");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const nothing = plan && !plan.added.length && !plan.removed.length && !plan.reordered;
  return (
    <>
      <Button
        size="sm"
        disabled={disabled || busy}
        onClick={() => void open()}
        data-testid="sync-enum"
        title="Make the bound enum's members the lifecycle's states"
      >
        <RefreshCcw /> Sync enum
      </Button>
      <Dialog open={plan !== null} onOpenChange={(o) => !o && setPlan(null)}>
        <DialogContent title="Sync enum" description="The bound enum's members become the lifecycle's states, in order.">
          <ul className="flex flex-col gap-0.5 text-12" data-testid="sync-enum-plan">
            {plan ? syncPlanLines(plan).map((l) => <li key={l}>{l}</li>) : null}
          </ul>
          {error || plan?.diagnostics.length ? (
            <p role="alert" className="text-12 text-danger">
              {error ?? plan?.diagnostics[0]?.message}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button onClick={() => setPlan(null)}>Cancel</Button>
            <Button variant="primary" disabled={busy || !!nothing || !!plan?.diagnostics.length} onClick={() => void apply()} data-testid="sync-enum-apply">
              Apply
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
