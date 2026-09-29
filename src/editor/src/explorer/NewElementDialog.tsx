// The New element dialog (explorer-redesign.md 1.8): one dialog for every New action of the explorer's menus, its New
// button, its empty state, the first-run panel and the palette. The store's `newElement` opens it with a kind and the
// domain its picker starts on (the row's, the selection's or the open diagram's). Saving creates the element, pushes an
// undo entry, shows its explorer, selects it (the explorer reveals it in its kind folder) and opens it.
import { useCallback, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { applySaveResult, keys } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ElementSummary, ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, Input, Select } from "@/components/ui/input";
import { GROUP_LABELS } from "@/model/labels";
import { BUILTIN_TYPES } from "@/model/model";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { useEditor } from "@/state/store";
import { createReferenceType } from "@/workspaces/reference-data/actions";
import { buildElement, CREATE_LABELS, CREATED_KIND, currentDomain, nameProblem, startDomain, takesDomain, type CreateKind } from "./create";
import { domainChoices } from "./dialogs";
import { useForest } from "./Explorer";
import type { Forest } from "./tree";

const DIALECTS = ["postgresql", "sqlserver", "mysql", "sqlite", "oracle"] as const;

const HINTS: Partial<Record<CreateKind, string>> = {
  package: "A domain groups elements; it also sets the output folder and namespace. Such as Billing.",
  "sub-package": "A domain inside another one. Such as Invoicing.",
  entity: "A PascalCase identifier, such as Shipment. The entity starts with a uuid key.",
  relation: "Such as CustomerOrders.",
  enum: "Such as OrderStatus.",
  "value-object": "Such as Address.",
  "scalar-type": "Such as Email.",
  "reference-type": "Such as Country. Its rows are managed on the Reference data screen.",
  diagram: "Such as Billing overview.",
  database: "Such as main.",
};

/** The dialog, mounted once by the app shell. */
export function NewElementHost() {
  const { store } = useServices();
  const request = useEditor(store, (s) => s.newElement);
  const { forest } = useForest();
  if (!request || !forest) return null;
  return <NewElementDialog key={`${request.kind}:${request.domain ?? ""}`} kind={request.kind} domain={request.domain} forest={forest} />;
}

function NewElementDialog({ kind, domain, forest }: { kind: CreateKind; domain: string | null; forest: Forest }) {
  const services = useServices();
  const { store } = services;
  const queryClient = useQueryClient();
  const { select, openDiagram, openDatabase, openWorkspace, openEditor } = useEditorNavigation();
  const [name, setName] = useState("");
  const [home, setHome] = useState(domain ?? "");
  const [base, setBase] = useState("string");
  const [dialect, setDialect] = useState<string>("postgresql");
  const entities = [...forest.byId.values()].filter((r) => r.kind === "entity").sort((a, b) => a.name.localeCompare(b.name));
  const inDomain = (id: string) => !home || forest.byId.get(id)?.package === home;
  const firstEntity = entities.find((e) => inDomain(e.id)) ?? entities[0];
  const [source, setSource] = useState(firstEntity?.id ?? "");
  // The second end starts on another entity so the default is not a self-relation with two identical roles.
  const secondEntity = entities.find((e) => e.id !== firstEntity?.id && inDomain(e.id)) ?? entities.find((e) => e.id !== firstEntity?.id) ?? firstEntity;
  const [target, setTarget] = useState(secondEntity?.id ?? "");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const close = () => store.getState().requestNew(null);
  const problem = nameProblem(kind, name);
  const needsEnds = kind === "relation" && (!source || !target);
  const label = CREATE_LABELS[kind];
  const domainLabel = kind === "package" || kind === "sub-package" ? "Parent domain" : "Domain";
  const choices = domainChoices(forest);

  const finish = (id: string, json: ModelJson | null, hash: string | null) => {
    const summary =
      queryClient.getQueryData<ElementSummary[]>(keys.index)?.find((r) => r.id === id) ??
      ({ id, kind: CREATED_KIND[kind], name: name.trim(), tags: [], stereotypes: [], ...(home ? { package: home } : {}) } as unknown as ElementSummary);
    const s = store.getState();
    if (json) s.pushUndo({ label: `${label} ${name.trim()}`, ids: [id], before: [null], after: [clone(json)], afterHashes: [hash] });
    close();
    if (kind === "diagram") {
      s.setSidebar("diagrams");
      openDiagram(id);
      select([id]);
    } else if (kind === "database") {
      s.setSidebar("databases");
      select([id]);
      openDatabase(id);
    } else if (kind === "reference-type") {
      s.setSidebar("reference-data");
      select([id]);
      openWorkspace("reference-data");
    } else {
      s.setSidebar("domain-model");
      openEditor(summary, true);
    }
    s.notify(`Created ${name.trim()}.`);
  };

  const create = async () => {
    if (problem || needsEnds || busy) return;
    setBusy(true);
    try {
      if (kind === "reference-type") {
        const outcome = await createReferenceType(services, { name: name.trim(), displayName: "", category: null });
        if (!outcome.ok) setError(outcome.reason);
        else finish(outcome.id, null, null);
        return;
      }
      const json = buildElement(
        kind,
        {
          name,
          domain: takesDomain(kind) ? home || null : null,
          base,
          dialect,
          source,
          target,
          sourceName: forest.byId.get(source)?.name,
          targetName: forest.byId.get(target)?.name,
        },
        newId,
      );
      const result = await endpoints.createElement(json);
      if (result.outcome !== "saved") {
        setError(result.diagnostics[0]?.message ?? result.outcome);
        return;
      }
      applySaveResult(queryClient, result);
      const id = String((json as unknown as { id: string }).id);
      finish(id, (result.current?.json as ModelJson | undefined) ?? json, result.hash);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && close()}>
      <DialogContent title={label}>
        <form
          className="flex flex-col gap-3"
          data-testid="new-element-dialog"
          data-kind={kind}
          onSubmit={(e) => {
            e.preventDefault();
            void create();
          }}
        >
          <Field label="Name" htmlFor="new-element-name" hint={HINTS[kind]}>
            <Input
              id="new-element-name"
              autoFocus
              value={name}
              onChange={(e) => setName(e.target.value)}
              aria-invalid={(name !== "" && !!problem) || undefined}
            />
          </Field>
          {name !== "" && problem ? <p className="text-12 text-danger">{problem}</p> : null}
          {takesDomain(kind) ? (
            <Field label={domainLabel} htmlFor="new-element-domain">
              <Select id="new-element-domain" value={home} onChange={(e) => setHome(e.target.value)}>
                <option value="">{kind === "package" || kind === "sub-package" ? "None (a top-level domain)" : GROUP_LABELS.notInDomain}</option>
                {choices.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.path}
                  </option>
                ))}
              </Select>
            </Field>
          ) : null}
          {kind === "scalar-type" ? (
            <Field label="Base type" htmlFor="new-element-base">
              <Select id="new-element-base" value={base} onChange={(e) => setBase(e.target.value)}>
                {BUILTIN_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {t}
                  </option>
                ))}
              </Select>
            </Field>
          ) : null}
          {kind === "database" ? (
            <Field label="Dialect" htmlFor="new-element-dialect">
              <Select id="new-element-dialect" value={dialect} onChange={(e) => setDialect(e.target.value)}>
                {DIALECTS.map((d) => (
                  <option key={d} value={d}>
                    {d}
                  </option>
                ))}
              </Select>
            </Field>
          ) : null}
          {kind === "relation" ? (
            entities.length ? (
              <>
                <Field label="From entity" htmlFor="new-element-source">
                  <Select id="new-element-source" value={source} onChange={(e) => setSource(e.target.value)}>
                    {entities.map((e) => (
                      <option key={e.id} value={e.id}>
                        {e.name}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="To entity" htmlFor="new-element-target">
                  <Select id="new-element-target" value={target} onChange={(e) => setTarget(e.target.value)}>
                    {entities.map((e) => (
                      <option key={e.id} value={e.id}>
                        {e.name}
                      </option>
                    ))}
                  </Select>
                </Field>
              </>
            ) : (
              <p className="text-12 text-secondary">Create an entity first: a relationship joins two entities.</p>
            )
          ) : null}
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!!problem || needsEnds || busy} data-testid="new-element-create">
              Create
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Buttons for New actions (the explorer's empty state and the first-run panel). */
export function CreateButtons({ kinds, domain, testid }: { kinds: readonly CreateKind[]; domain?: string | null; testid: string }) {
  const { store } = useServices();
  return (
    <div className="flex flex-wrap justify-center gap-2" data-testid={testid}>
      {kinds.map((k) => (
        <Button
          key={k}
          size="sm"
          onClick={() => store.getState().requestNew({ kind: k, domain: startDomain(k, domain ?? null) })}
          data-testid={`${testid}-${k}`}
        >
          <Plus /> {CREATE_LABELS[k]}
        </Button>
      ))}
    </div>
  );
}

/** The domain a New action taken outside the explorer starts on (the selection, else the open diagram's home). */
export function useCurrentDomain(): () => string | null {
  const { store } = useServices();
  const { forest } = useForest();
  return useCallback(() => {
    const s = store.getState();
    return currentDomain(forest, { selection: s.selection, activeDiagram: s.activeDiagram });
  }, [store, forest]);
}
