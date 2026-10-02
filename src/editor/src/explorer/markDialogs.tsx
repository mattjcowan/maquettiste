// The explorer's mark and promotion dialogs (explorer-redesign.md 1.8): Apply stereotype…, Tag… and Set category…
// on a row or a multi-selection (the choices are the vocabularies of the selection's domain chain, §1.11), and
// Promote to entity on a value object or a custom type (explorer/promote.ts plans the batch).
import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { applyBatchResult, elementQuery } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ElementDocument, ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { useVocabularies } from "@/inspector/fields";
import { commonDomain } from "@/model/vocabularies";
import { clone } from "@/lib/json";
import { newId } from "@/lib/ids";
import { applyMark, MARK_LABELS, markLabel, useBatchEdit, type MarkKind } from "./marks";
import { planPromotion, type PromotePlan, type PromoteSource, type PromoteUser } from "./promote";
import type { Forest } from "./tree";

export function MarkDialog({ forest, request, onClose }: { forest: Forest; request: { kind: MarkKind; ids: string[] } | null; onClose: () => void }) {
  const ids = request?.ids ?? [];
  const rows = ids.map((id) => forest.byId.get(id)).filter((r): r is NonNullable<typeof r> => !!r);
  const kinds = [...new Set(rows.map((r) => r.kind))];
  const domain = commonDomain(
    rows.map((r) => (r.kind === "package" ? r.id : (r.package ?? null))),
    [...forest.byId.values()] as never,
  );
  const vocab = useVocabularies(kinds.length === 1 ? kinds[0] : "entity", domain);
  const batchEdit = useBatchEdit();
  const [value, setValue] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => setValue(""), [request]);
  if (!request) return null;
  const labels = MARK_LABELS[request.kind];
  const options =
    request.kind === "stereotype"
      ? vocab.stereotypes.map((s) => ({ value: s.key, label: `«${s.key}»` }))
      : request.kind === "tag"
        ? vocab.tags.map((t) => ({ value: t.value, label: t.label }))
        : vocab.categories.map((c) => ({ value: c.value, label: c.label }));
  const title = rows.length === 1 ? `${labels.title}: ${rows[0].name}` : `${labels.title}: ${rows.length} elements`;
  const submit = async () => {
    const mark = request.kind === "category" ? { kind: "category" as const, id: value || null } : { kind: request.kind, key: value };
    if (mark.kind !== "category" && !mark.key) return;
    setBusy(true);
    try {
      const label = markLabel(mark, options.find((o) => o.value === value)?.label);
      if (await batchEdit(label, ids, (json) => applyMark(json, mark))) onClose();
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={title} description="The choices are the vocabularies of the selection's domain and its parents, then the global ones.">
        <form
          className="flex flex-col gap-2"
          data-testid={`mark-dialog-${request.kind}`}
          onSubmit={(e) => {
            e.preventDefault();
            void submit();
          }}
        >
          <Field label={labels.field} htmlFor="mark-value">
            <Select id="mark-value" value={value} onChange={(e) => setValue(e.target.value)} autoFocus>
              <option value="">{request.kind === "category" ? "(no category)" : "Choose…"}</option>
              {options.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          {options.length ? null : (
            <p className="text-12 text-secondary">
              {request.kind === "stereotype"
                ? "No stereotype applies to this kind."
                : `No ${request.kind} is declared for this domain; add one under Settings.`}
            </p>
          )}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={busy || (request.kind !== "category" && !value)} data-testid="mark-apply">
              {labels.action}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

interface Loaded {
  source: ElementDocument;
  users: ElementDocument[];
  plan: PromotePlan;
}

/** Promote to entity: shows what the rewrite does, then saves it as one batch with one undo entry. */
export function PromoteDialog({ id, onClose, onDone }: { id: string | null; onClose: () => void; onDone: (entity: string) => void }) {
  const qc = useQueryClient();
  const { store, drafts } = useServices();
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    setLoaded(null);
    if (!id) return;
    let live = true;
    void (async () => {
      await drafts.flushAll();
      const source = await qc.fetchQuery(elementQuery(id));
      const refs = await endpoints.getReferences(id);
      const userIds = [...new Set(refs.map((r) => r.fromElementId))].filter((x) => x !== id).sort();
      const users = (await Promise.all(userIds.map((u) => qc.fetchQuery(elementQuery(u))))).filter((d): d is ElementDocument => !!d);
      // The owners' mapping elements, seeds and queries, which may point at an attribute the promotion removes.
      const owners = users.filter((u) => u.json.kind === "entity").map((u) => u.element.id);
      const ownerRefs = (await Promise.all(owners.map((o) => endpoints.getReferences(o)))).flat();
      const depIds = [...new Set(ownerRefs.map((r) => r.fromElementId))].filter((x) => x !== id && !userIds.includes(x)).sort();
      const dependents = (await Promise.all(depIds.map((d) => qc.fetchQuery(elementQuery(d))))).filter(
        (d): d is ElementDocument => !!d && (d.json.kind === "mapping" || d.json.kind === "seed" || d.json.kind === "query"),
      );
      if (!live || !source) return;
      const plan = planPromotion(
        source.json as unknown as PromoteSource,
        users.map((u) => u.json as unknown as PromoteUser),
        newId,
        dependents.map((d) => d.json as unknown as PromoteUser),
      );
      setLoaded({ source, users: [...users, ...dependents], plan });
    })();
    return () => {
      live = false;
    };
  }, [id, qc, drafts]);
  if (!id) return null;
  const name = String(loaded?.source.json.name ?? "");
  const promote = async () => {
    if (!loaded || loaded.plan.blocked.length) return;
    const { plan, source, users } = loaded;
    setBusy(true);
    try {
      const hashOf = new Map(users.map((u) => [u.element.id, u.hash]));
      const beforeOf = new Map(users.map((u) => [u.element.id, u.json]));
      const result = await endpoints.applyBatch({
        operations: [
          { op: "create" as const, element: plan.entity as never },
          ...plan.relations.map((r) => ({ op: "create" as const, element: r as never })),
          ...plan.updates.map((u) => ({ op: "update" as const, id: u.id, expectedHash: hashOf.get(u.id)!, element: u.json as never })),
          { op: "delete" as const, id: source.element.id, expectedHash: source.hash },
        ],
      });
      if (!endpoints.isBatchResult(result) || result.outcome !== "saved") {
        const why = endpoints.isBatchResult(result)
          ? (result.items.find((i) => i.diagnostics.length)?.diagnostics[0]?.message ?? result.outcome)
          : "invalid batch";
        store.getState().notify(`${name} was not promoted: ${why}`, "error");
        return;
      }
      applyBatchResult(qc, result);
      const created = [plan.entity, ...plan.relations];
      store.getState().pushUndo({
        label: `Promote ${name} to entity`,
        ids: [...created.map((c) => String(c.id)), ...plan.updates.map((u) => u.id), source.element.id],
        before: [...created.map(() => null), ...plan.updates.map((u) => clone(beforeOf.get(u.id) as ModelJson)), clone(source.json as ModelJson)],
        after: [...created.map((c) => clone(c as unknown as ModelJson)), ...plan.updates.map((u) => clone(u.json as unknown as ModelJson)), null],
        afterHashes: result.items.map((i, n) => (n === result.items.length - 1 ? null : i.hash)),
      });
      store
        .getState()
        .notify(
          plan.relations.length
            ? `Promoted ${name} to an entity; ${plan.relations.length === 1 ? "1 use became a relationship" : `${plan.relations.length} uses became relationships`}.`
            : `Promoted ${name} to an entity.`,
        );
      onDone(String(plan.entity.id));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={loaded ? `Promote ${name} to entity` : "Promote to entity"}
        description="The element becomes an entity with an id key; each attribute typed as it becomes a relationship."
      >
        <div className="flex flex-col gap-2 text-13" data-testid="promote-dialog">
          {!loaded ? <p className="text-secondary">Finding the uses…</p> : null}
          {loaded && loaded.plan.blocked.length ? (
            <div className="text-danger" data-testid="promote-blocked">
              <p>These uses cannot be rewritten; change them first:</p>
              <ul className="list-disc pl-4">
                {loaded.plan.blocked.map((b) => (
                  <li key={b}>{b}</li>
                ))}
              </ul>
            </div>
          ) : null}
          {loaded && !loaded.plan.blocked.length ? (
            loaded.plan.relations.length ? (
              <ul className="list-disc pl-4" aria-label="Uses rewritten" data-testid="promote-uses">
                {loaded.plan.relations.map((r) => (
                  <li key={String(r.id)}>New relationship {String(r.name)}</li>
                ))}
                {loaded.plan.rewritten.map((r) => (
                  <li key={r}>{r}</li>
                ))}
              </ul>
            ) : loaded.plan.rewritten.length ? (
              <ul className="list-disc pl-4" aria-label="Uses rewritten" data-testid="promote-uses">
                {loaded.plan.rewritten.map((r) => (
                  <li key={r}>{r}</li>
                ))}
              </ul>
            ) : (
              <p className="text-secondary">No attribute uses {name}.</p>
            )
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button
              type="button"
              variant="primary"
              disabled={busy || !loaded || loaded.plan.blocked.length > 0}
              onClick={() => void promote()}
              data-testid="promote-confirm"
            >
              Promote
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
