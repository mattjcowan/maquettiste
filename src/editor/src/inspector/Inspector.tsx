import { useEffect, useMemo, useState } from "react";
import { CircleAlert, Loader2, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { applyBatchResult, applySaveResult, keys, useIndex, useProject, useReferences } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ElementKind, EntityDoc, ModelJson, ReferenceInfo, StereotypeDoc } from "@/api/types";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Badge, EmptyState, SectionTitle, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Select, Field } from "@/components/ui/input";
import { CodeView } from "@/code";
import { useEditor } from "@/state/store";
import { displayName, KIND_LABELS } from "@/model/model";
import { indexLookup } from "@/model/index";
import { clone } from "@/lib/json";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { KindIcon } from "@/app/icons";
import { useDraftDocument } from "./useDraft";
import {
  AttributesOnlyFields,
  CommonFields,
  DatabaseFields,
  EntityFields,
  EnumFields,
  RelationFields,
  ScalarFields,
  useVocabularies,
  type FormProps,
} from "./fields";
import { applicableExtensions, SchemaForm } from "./SchemaForm";

export function Inspector() {
  const { store } = useServices();
  const selection = useEditor(store, (s) => s.selection);
  if (selection.length === 0)
    return (
      <section aria-label="Inspector" className="h-full bg-surface">
        <EmptyState title="Nothing selected">Select an element in the explorer or on the canvas to see and edit its properties.</EmptyState>
      </section>
    );
  if (selection.length > 1) return <BulkInspector ids={selection} />;
  return <ElementInspector key={selection[0]} id={selection[0]} />;
}

function statusBadge(status: string | undefined, saving: boolean) {
  if (saving || status === "saving")
    return (
      <Badge>
        <Loader2 className="size-3 animate-spin motion-reduce:animate-none" aria-hidden /> Saving
      </Badge>
    );
  if (status === "dirty") return <Badge tone="accent">Unsaved</Badge>;
  if (status === "invalid") return <Badge tone="danger">Not saved: invalid</Badge>;
  if (status === "conflict") return <Badge tone="warning">Conflict</Badge>;
  return <Badge tone="success">Saved</Badge>;
}

function ElementInspector({ id }: { id: string }) {
  const { element, draft, json, edit, flush } = useDraftDocument(id);
  const project = useProject();
  const index = useIndex();
  const { store } = useServices();
  const focus = useEditor(store, (s) => s.focus);
  const [tab, setTab] = useState("properties");
  const kind = (json as { kind?: ElementKind } | undefined)?.kind;
  const vocab = useVocabularies(kind ?? "entity");
  const summary = index.data?.find((r) => r.id === id);

  useEffect(() => {
    if (!focus?.pointer || focus.id !== id) return;
    // Reveal the field a problem points at: grid cells carry data-cell, inputs their ids.
    const match = /^\/attributes\/(\d+)(?:\/(\w+))?/.exec(focus.pointer);
    requestAnimationFrame(() => {
      const target = match
        ? document.querySelector<HTMLElement>(`[data-testid="attribute-grid"] [data-cell^="${match[1]}:"][data-column="${match[2] ?? "name"}"]`)
        : document.getElementById(`${id}-${focus.pointer!.split("/")[1]}`);
      target?.scrollIntoView({ block: "nearest" });
      target?.focus();
    });
  }, [focus, id]);

  if (element.isPending && !json) return <Spinner label="Loading element" />;
  if (element.error && !json) return <EmptyState title="This element could not be loaded">{String((element.error as Error).message)}</EmptyState>;
  if (!json || !kind) return null;

  const diagnostics = draft?.diagnostics ?? [];
  const props: FormProps = { id, json, doc: element.data, edit, flush: () => void flush(), diagnostics };
  const stereotypeKeys = ((json as { stereotypes?: string[] }).stereotypes ?? []) as string[];
  const extensions = applicableExtensions(project.data?.extensions ?? [], kind, stereotypeKeys);
  const defaults: Record<string, { value: unknown; from: string }> = {};
  for (const key of stereotypeKeys) {
    const s = vocab.allStereotypes.find((x) => x.key === key) as StereotypeDoc | undefined;
    for (const [name, value] of Object.entries((s?.defaultProperties as Record<string, unknown> | undefined) ?? {})) defaults[name] ??= { value, from: key };
  }

  return (
    <section
      aria-label={`Inspector: ${displayName(summary ?? { name: String((json as { name?: string }).name ?? ""), kind, id })}`}
      className="flex h-full min-h-0 flex-col bg-surface"
      data-testid="inspector"
    >
      <header className="flex flex-col gap-1 border-b border-default px-3 py-2">
        <div className="flex items-center gap-2">
          <KindIcon kind={kind} />
          <h2 className="min-w-0 flex-1 truncate text-14 font-semibold" data-testid="inspector-title">
            {String((json as { name?: string }).name ?? "") || displayName(summary)}
          </h2>
          <span data-testid="save-status">{statusBadge(draft?.status, false)}</span>
          <DeleteButton id={id} name={String((json as { name?: string }).name ?? id)} />
        </div>
        <p className="truncate font-mono text-11 text-secondary">{element.data?.path ?? KIND_LABELS[kind]}</p>
        {draft?.error ? (
          <p role="alert" className="text-12 text-danger">
            {draft.error}
          </p>
        ) : null}
        {draft?.status === "invalid" && diagnostics.length ? (
          <ul role="alert" className="flex flex-col gap-0.5 text-12 text-danger" data-testid="draft-diagnostics">
            {diagnostics.map((d, i) => (
              <li key={i} className="flex gap-1">
                <CircleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
                <span>
                  <span className="font-mono">{d.rule}</span> {d.message}
                </span>
              </li>
            ))}
          </ul>
        ) : null}
      </header>
      <Tabs value={tab} onValueChange={setTab} className="flex min-h-0 flex-1 flex-col">
        <TabsList aria-label="Inspector views">
          <TabsTrigger value="properties">Properties</TabsTrigger>
          <TabsTrigger value="json">JSON</TabsTrigger>
          <TabsTrigger value="references">Where used</TabsTrigger>
        </TabsList>
        <TabsContent value="properties" className="overflow-auto p-3">
          <div className="flex flex-col gap-4">
            <CommonFields {...props} />
            {kind === "entity" ? <EntityFields {...props} /> : null}
            {kind === "relation" ? <RelationFields {...props} /> : null}
            {kind === "value-object" || kind === "stereotype" ? <AttributesOnlyFields {...props} /> : null}
            {kind === "enum" ? <EnumFields {...props} /> : null}
            {kind === "scalar-type" ? <ScalarFields {...props} /> : null}
            {kind === "database" ? <DatabaseFields {...props} /> : null}
            {extensions.length ? (
              <div className="flex flex-col gap-2">
                <SectionTitle>Custom properties</SectionTitle>
                <SchemaForm
                  extensions={extensions}
                  json={json}
                  defaults={defaults}
                  onChange={(name, value) =>
                    edit((j) => {
                      const record = j as { properties?: Record<string, unknown> };
                      const next = { ...(record.properties ?? {}) };
                      if (value === undefined) delete next[name];
                      else next[name] = value;
                      if (Object.keys(next).length) record.properties = next;
                      else delete record.properties;
                    })
                  }
                />
              </div>
            ) : null}
            {!["entity", "relation", "value-object", "stereotype", "enum", "scalar-type", "database", "package", "diagram"].includes(kind) ? (
              <p className="text-12 text-secondary">Every other property of this {KIND_LABELS[kind].toLowerCase()} is editable in the JSON view.</p>
            ) : null}
          </div>
        </TabsContent>
        <TabsContent value="json" className="flex min-h-0 flex-col p-0">
          <JsonTab json={json} onChange={(next) => edit(() => next)} />
        </TabsContent>
        <TabsContent value="references" className="overflow-auto p-3">
          <References id={id} />
        </TabsContent>
      </Tabs>
    </section>
  );
}

function JsonTab({ json, onChange }: { json: ModelJson; onChange: (json: ModelJson) => void }) {
  const [error, setError] = useState<string | null>(null);
  const text = useMemo(() => JSON.stringify(json, null, 2), [json]);
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {error ? (
        <p role="alert" className="border-b border-default px-3 py-1 text-12 text-danger">
          {error}
        </p>
      ) : null}
      <div className="min-h-0 flex-1">
        <CodeView
          language="json"
          label="Element JSON"
          value={text}
          onChange={(value) => {
            try {
              const parsed = JSON.parse(value) as ModelJson;
              setError(null);
              if (JSON.stringify(parsed) !== JSON.stringify(json)) onChange(parsed);
            } catch (e) {
              setError(`Not valid JSON yet: ${(e as Error).message}`);
            }
          }}
        />
      </div>
    </div>
  );
}

function References({ id }: { id: string }) {
  const refs = useReferences(id);
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const { reveal } = useEditorNavigation();
  if (refs.isPending) return <Spinner label="Finding references" />;
  if (!refs.data?.length) return <EmptyState title="Nothing references this element" />;
  return (
    <ul className="flex flex-col gap-1" aria-label="References to this element">
      {refs.data.map((r: ReferenceInfo, i) => {
        const from = lookup.byId.get(r.fromElementId);
        return (
          <li key={i}>
            <button
              type="button"
              className="flex w-full items-center gap-2 rounded-control px-2 py-1 text-left text-13 hover:bg-accent-subtle"
              onClick={() => from && reveal(from, r.jsonPointer)}
            >
              {from ? <KindIcon kind={from.kind} /> : null}
              <span className="flex-1 truncate">{displayName(from)}</span>
              <span className="font-mono text-11 text-secondary">{r.jsonPointer}</span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

function DeleteButton({ id, name }: { id: string; name: string }) {
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const { select } = useEditorNavigation();
  const [referrers, setReferrers] = useState<ReferenceInfo[] | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (resolution: "refuse" | "remove-references") => {
    const doc = qc.getQueryData<{ hash: string; json: ModelJson }>(keys.element(id));
    if (!doc) return;
    setBusy(true);
    try {
      await drafts.flush(id);
      const current = qc.getQueryData<{ hash: string; json: ModelJson }>(keys.element(id)) ?? doc;
      const result = await endpoints.deleteElement(id, current.hash, resolution);
      if (result.outcome === "referenced") setReferrers(result.referrers);
      else if (result.outcome === "saved") {
        applySaveResult(qc, result);
        drafts.discard(id);
        setReferrers(null);
        if (resolution === "refuse")
          store.getState().pushUndo({ label: `Delete ${name}`, ids: [id], before: [clone(current.json)], after: [null], afterHashes: [null] });
        else store.getState().notify(`Deleted ${name} and cleared its references. This delete cannot be undone from the editor.`);
        select([]);
      } else store.getState().notify(`Delete refused: ${result.outcome}${result.diagnostics[0] ? ` — ${result.diagnostics[0].message}` : ""}`, "error");
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <Button size="icon-sm" variant="ghost" aria-label={`Delete ${name}`} disabled={busy} onClick={() => void run("refuse")}>
        <Trash2 />
      </Button>
      <Dialog open={referrers !== null} onOpenChange={(open) => !open && setReferrers(null)}>
        <DialogContent title={`${name} is still referenced`} description={`${referrers?.length ?? 0} references block the delete.`}>
          <ul className="max-h-60 overflow-auto font-mono text-12">
            {referrers?.map((r, i) => (
              <li key={i}>
                {r.fromElementId} {r.jsonPointer}
              </li>
            ))}
          </ul>
          <p className="text-12 text-secondary">
            Removing references clears optional ones (such as diagram members); a required reference makes the delete invalid.
          </p>
          <div className="flex justify-end gap-2">
            <Button onClick={() => setReferrers(null)}>Keep it</Button>
            <Button variant="danger" onClick={() => void run("remove-references")}>
              Delete and remove references
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}

/** Multi-select: bulk edits as one batch (apply a stereotype or a tag to many elements). */
function BulkInspector({ ids }: { ids: string[] }) {
  const { store, queryClient, drafts } = useServices();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const rows = ids.map((id) => lookup.byId.get(id)).filter((r): r is NonNullable<typeof r> => !!r);
  const kinds = [...new Set(rows.map((r) => r.kind))];
  const vocab = useVocabularies(kinds.length === 1 ? kinds[0] : "entity");
  const [stereotype, setStereotype] = useState("");
  const [tag, setTag] = useState("");
  const [busy, setBusy] = useState(false);

  const apply = async (label: string, mutate: (json: ModelJson) => void) => {
    setBusy(true);
    try {
      await drafts.flushAll();
      const docs = await Promise.all(ids.map((id) => queryClient.fetchQuery({ queryKey: keys.element(id), queryFn: () => endpoints.getElement(id) })));
      const after = docs.map((d) => {
        const json = clone(d.json as ModelJson);
        mutate(json);
        return json;
      });
      const result = await endpoints.applyBatch({
        operations: docs.map((d, i) => ({ op: "update" as const, id: ids[i], expectedHash: d.hash, element: after[i] as never })),
      });
      if (!endpoints.isBatchResult(result) || result.outcome !== "saved") {
        store.getState().notify(`${label} failed: ${endpoints.isBatchResult(result) ? result.outcome : "invalid batch"}`, "error");
        return;
      }
      applyBatchResult(queryClient, result);
      store.getState().pushUndo({ label, ids, before: docs.map((d) => clone(d.json as ModelJson)), after, afterHashes: result.items.map((i) => i.hash) });
      store.getState().notify(`${label}: ${ids.length} elements saved in one batch.`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-label="Inspector: bulk edit" className="flex h-full flex-col gap-4 bg-surface p-3" data-testid="bulk-inspector">
      <h2 className="text-14 font-semibold">{ids.length} elements selected</h2>
      <ul className="max-h-40 overflow-auto text-12 text-secondary">
        {rows.map((r) => (
          <li key={r.id}>{displayName(r)}</li>
        ))}
      </ul>
      <Field label="Apply a stereotype" htmlFor="bulk-stereotype">
        <div className="flex gap-2">
          <Select id="bulk-stereotype" value={stereotype} onChange={(e) => setStereotype(e.target.value)}>
            <option value="">Choose…</option>
            {vocab.stereotypes.map((s) => (
              <option key={s.key} value={s.key}>
                «{s.key}»
              </option>
            ))}
          </Select>
          <Button
            variant="primary"
            disabled={!stereotype || busy}
            onClick={() =>
              void apply(`Apply «${stereotype}»`, (json) => {
                const e = json as EntityDoc;
                e.stereotypes = [...new Set([...(e.stereotypes ?? []), stereotype])];
              })
            }
          >
            Apply
          </Button>
        </div>
      </Field>
      <Field label="Add a tag" htmlFor="bulk-tag">
        <div className="flex gap-2">
          <Select id="bulk-tag" value={tag} onChange={(e) => setTag(e.target.value)}>
            <option value="">Choose…</option>
            {vocab.tags.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </Select>
          <Button
            variant="primary"
            disabled={!tag || busy}
            onClick={() =>
              void apply(`Tag ${tag}`, (json) => {
                const e = json as EntityDoc;
                e.tags = [...new Set([...(e.tags ?? []), tag])];
              })
            }
          >
            Add
          </Button>
        </div>
      </Field>
    </section>
  );
}
