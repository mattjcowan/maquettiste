// The process editor's top controls (phase-3-design.md 6.2), also the inspector's Process section: name, domain, use,
// subject, the bound attribute with its MQ9203 badge and Sync enum (a dry run shown in a small confirm, then applied
// as one undo step), and the stereotype, tag and category chips.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { RefreshCcw } from "lucide-react";
import { useElement, useIndex } from "@/api/queries";
import type { AttributeDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Select } from "@/components/ui/input";
import { Badge } from "@/components/ui/misc";
import { useServices } from "@/app/context";
import { indexLookup } from "@/model/index";
import { syncPlanLines } from "@/model/process";
import { MarkChips, NameAndDomain, domIdOf, type EditorContext } from "../EditorFrame";
import { applySyncEnum, setLifecycle, syncEnum, type SyncEnumResult } from "./api";
import { useProblems } from "./shared";

type Rec = Record<string, unknown>;

/** The subject's enum-typed attributes, the bound enum's id and its member names (null until loaded). */
export function useBinding(subject: string | undefined, bound: string | undefined) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const entity = useElement(subject ?? null);
  const attributes = ((entity.data?.json as { attributes?: AttributeDoc[] } | undefined)?.attributes ?? []).filter((a) => {
    const ref = typeof a.type === "object" && a.type ? (a.type as { ref?: string }).ref : undefined;
    return !!ref && lookup.byId.get(ref)?.kind === "enum";
  });
  const attribute = attributes.find((a) => a.id === bound);
  const enumId = attribute ? (attribute.type as { ref: string }).ref : null;
  const enumDoc = useElement(enumId);
  const members = enumDoc.data ? (((enumDoc.data.json as { members?: { name: string }[] }).members ?? []).map((m) => m.name) as string[]) : null;
  return { attributes, enumId, members };
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
  const entities = indexLookup(index.data).ofKind("entity");
  const use = rec.use === "lifecycle" ? "lifecycle" : "orchestration";
  const subject = typeof rec.subject === "string" ? rec.subject : undefined;
  const bound = typeof rec.boundAttribute === "string" ? rec.boundAttribute : undefined;
  const { attributes, enumId } = useBinding(subject, bound);
  const problems = useProblems(id, ctx.diagnostics);
  const drift = problems.find((d) => d.rule === "MQ9203");
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const subjectDoc = useElement(subject ?? null);
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
    const bound = (subjectDoc.data?.json as Rec | undefined)?.lifecycle === id;
    if (subject && bound) bind(subject, undefined, `Make ${name} an orchestration`);
    else set("use", undefined);
  };
  const setSubject = (next: string | undefined) => {
    if (use === "lifecycle" && next) bind(next, id, `Set the subject of ${name}`);
    else set("subject", next);
  };
  const set = (key: string, value: string | undefined) => {
    edit((j) => {
      const r = j as Rec;
      if (value) r[key] = value;
      else delete r[key];
      if (key === "use" && value !== "lifecycle") delete r.boundAttribute;
      if (key === "subject") delete r.boundAttribute;
    });
    flush();
  };
  return (
    <>
      <Field label="Use" htmlFor={`${dom}-use`}>
        <Select id={`${dom}-use`} value={use} onChange={(e) => setUse(e.target.value)}>
          <option value="orchestration">Orchestration</option>
          <option value="lifecycle" disabled={!subject}>
            {subject ? "Lifecycle" : "Lifecycle (choose a subject first)"}
          </option>
        </Select>
      </Field>
      <Field label="Subject" htmlFor={`${dom}-subject`}>
        <Select id={`${dom}-subject`} value={subject ?? ""} onChange={(e) => setSubject(e.target.value || undefined)}>
          {use === "lifecycle" && subject ? null : <option value="">{use === "lifecycle" ? "(choose an entity)" : "(none)"}</option>}
          {entities.map((e) => (
            <option key={e.id} value={e.id}>
              {e.name}
            </option>
          ))}
        </Select>
      </Field>
      {use === "lifecycle" ? (
        <Field label="Bound attribute" htmlFor={`${dom}-bound`}>
          <div className="flex items-center gap-1">
            <Select id={`${dom}-bound`} className="min-w-0 flex-1" value={bound ?? ""} onChange={(e) => set("boundAttribute", e.target.value || undefined)}>
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
            <SyncEnumButton process={id} enumId={enumId} disabled={!bound || !enumId} />
          </div>
        </Field>
      ) : null}
    </>
  );
}

/** Sync enum: the dry run's added, removed, reordered and refused members in a confirm, then the apply. */
export function SyncEnumButton({ process, enumId, disabled }: { process: string; enumId: string | null; disabled: boolean }) {
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
      setPlan(await syncEnum(process, true));
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
      const result = await applySyncEnum(qc, store, process, enumId);
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
