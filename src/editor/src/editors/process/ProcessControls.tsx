// The process editor's top controls (phase-3-design.md 6.2), also the inspector's Process section: name, domain, use,
// subject, the bound attribute with its MQ9203 badge and Sync enum (a dry run shown in a small confirm, then applied
// as one undo step), and the stereotype, tag and category chips.
import { PROCESS_LABELS } from "@/model/labels";
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { RefreshCcw } from "lucide-react";
import { useElement, useElements, useIndex } from "@/api/queries";
import type { AttributeDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { Badge } from "@/components/ui/misc";
import { useServices } from "@/app/context";
import { bindableAttributes, classifyAttribute, codeKindOf } from "@/model/binding";
import { indexLookup } from "@/model/index";
import { subjectsOf, syncPlanLines } from "@/model/process";
import { MarkChips, NameAndDomain, domIdOf, type EditorContext } from "../EditorFrame";
import { applySyncEnum, demoteLifecycle, replaceSoleSubject, setLifecycle, syncEnum, type SyncEnumResult } from "./api";
import { entryIdsOf, useProblems } from "./shared";

type Rec = Record<string, unknown>;

/**
 * The subject's attributes a lifecycle may bind (own, inherited or from a stereotype; single-valued, typed by a
 * non-flags enum, a reference type, or a plain status; lifecycle-design.md decision 11), the bound enum's id and its
 * member names (null until loaded). `enumId` and `members` stay enum-only.
 */
export function useBinding(subject: string | undefined, bound: string | undefined) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const chain: string[] = [];
  for (let id = subject; id && !chain.includes(id); id = lookup.byId.get(id)?.base) chain.push(id);
  const hasStereotypes = chain.some((id) => (lookup.byId.get(id)?.stereotypes ?? []).length > 0);
  const stereotypeIds = hasStereotypes ? lookup.ofKind("stereotype").map((s) => s.id) : [];
  const held = useElements([...chain, ...stereotypeIds]).byId;
  const json = (id: string) => held.get(id)?.json as Rec | undefined;
  const stereotype = (key: string) => stereotypeIds.map(json).find((s) => s?.key === key);
  const candidates = bindableAttributes(json(subject ?? ""), json, stereotype);
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
  const bound = sole?.boundAttribute;
  const listForm = Array.isArray(rec.subjects);
  const { attributes, enumId } = useBinding(subject, bound);
  const problems = useProblems(id, ctx.diagnostics, entryIdsOf(rec));
  const drift = problems.find((d) => d.rule === "MQ9203");
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const name = typeof rec.name === "string" ? rec.name : id;
  // A lifecycle binds both sides: Use and Subject changes that make or keep it a lifecycle, or leave one, go through
  // the set-lifecycle operation (the process, the entity and any previous partner in one batch, one undo step).
  const bind = (entity: string, target: string | undefined, label: string) =>
    void setLifecycle(qc, store, drafts, { process: id, entity, target, label }).then((refused) => {
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
  const setSubject = (next: string | undefined) => {
    if (!listForm && !(all.length === 0 && next)) {
      if (use === "lifecycle" && next) bind(next, id, `Set the subject of ${name}`);
      else set("subject", next);
      return;
    }
    // No subject yet: it gets `subjects`, from set-lifecycle for a lifecycle (decision 7). With a list: edit its one entry.
    if (all.length === 0 && use === "lifecycle" && next) bind(next, id, `Set the subject of ${name}`);
    else if (use === "lifecycle" && next && Array.isArray(rec.subjects) && rec.subjects.length === 1)
      void replaceSoleSubject(qc, store, drafts, { process: id, entity: next, label: `Set the subject of ${name}` }).then((refused) => {
        if (refused) store.getState().notify(`Set the subject of ${name}: ${refused}`, "error");
      });
    else {
      edit((j) => {
        const r = j as Rec;
        if (!next) delete r.subjects;
        else if (Array.isArray(r.subjects) && r.subjects.length === 1) r.subjects = [{ id: (r.subjects[0] as Rec).id, entity: next }];
        else r.subjects = [{ entity: next }];
      });
      flush();
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
      </Field>
      <Field label="Subject" htmlFor={`${dom}-subject`}>
        {many ? (
          <Input
            id={`${dom}-subject`}
            readOnly
            value={all.map((e) => lookup.byId.get(e.entity)?.name ?? e.entity).join(", ")}
            title="Several subjects are edited in the JSON view."
            data-testid="process-subjects"
          />
        ) : (
          <Select id={`${dom}-subject`} value={subject ?? ""} onChange={(e) => setSubject(e.target.value || undefined)}>
            {use === "lifecycle" && subject ? null : <option value="">{use === "lifecycle" ? "(choose an entity)" : "(none)"}</option>}
            {entities.map((e) => (
              <option key={e.id} value={e.id}>
                {e.name}
              </option>
            ))}
          </Select>
        )}
      </Field>
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
            <SyncEnumButton process={id} enumId={enumId} entity={listForm ? subject : undefined} disabled={!bound || !enumId} />
          </div>
        </Field>
      ) : null}
    </>
  );
}

/** Sync enum: the dry run's added, removed, reordered and refused members in a confirm, then the apply. */
export function SyncEnumButton({ process, enumId, entity, disabled }: { process: string; enumId: string | null; entity?: string; disabled: boolean }) {
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
      setPlan(await syncEnum(process, true, entity));
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
