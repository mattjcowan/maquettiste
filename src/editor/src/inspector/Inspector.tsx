import { useCallback, useEffect, useMemo, useState } from "react";
import { TranslationsSection } from "@/l10n/TranslationsSection";
import { CircleAlert, Loader2, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { useIndex, usePack, useProject, useReferences } from "@/api/queries";
import type { DeletePlan, DeleteResolution, ElementKind, EntityDoc, ModelJson, ReferenceInfo, StereotypeDoc } from "@/api/types";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Badge, EmptyState, SectionTitle, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { Select, Field } from "@/components/ui/input";
import { CodeView } from "@/code";
import { useEditor } from "@/state/store";
import { displayName, KIND_LABELS } from "@/model/model";
import { indexLookup } from "@/model/index";
import { commonDomain } from "@/model/vocabularies";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { KindIcon } from "@/app/icons";
import { useDraftDocument } from "./useDraft";
import { deletesAlone, DeletePlanDialog, loadDeletePlans, runPlannedDelete, type DeletePlans } from "./DeletePlanView";
import { useBatchEdit } from "@/explorer/marks";
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
import { emptyTitle, inspectorContext, type InspectorContext } from "./context";
import { OpenTableButton, TableInspector } from "./TableInspector";
import { DatabaseTypeFields, RoutineFields, SchemaField, SequenceFields, SqlObjectFields, ViewFields } from "@/editors/database/fields";
import { EntityAttributeList } from "./AttributeList";
import { ActorInspectorSection, ProcessInspectorSection, ScenarioInspectorSection, useProcessNodeShown } from "./ProcessSections";
import { attributesView, INSPECTOR_TAB_LABELS, inspectorTabs, resolveInspectorTab } from "./tabs";

/** The inspector's context from the store (see ./context): the pieces are stable references, the context is derived. */
export function useInspectorContext(): InspectorContext {
  const { store } = useServices();
  const workspace = useEditor(store, (s) => s.workspace);
  const active = useEditor(store, (s) => s.explorer.active);
  const editors = useEditor(store, (s) => s.editors);
  const generation = useEditor(store, (s) => s.generation);
  const selectionBy = useEditor(store, (s) => s.selectionBy);
  const pinned = useEditor(store, (s) => s.explorer.pinned);
  const selectionFrom = useEditor(store, (s) => s.selectionFrom);
  const inspectedTable = useEditor(store, (s) => s.inspectedTable);
  const index = useIndex();
  const exists = useMemo(() => {
    if (!index.data) return undefined;
    const byId = indexLookup(index.data).byId;
    return (id: string) => byId.has(id);
  }, [index.data]);
  // A deleted element (here, in another session, or by an undo of its create) leaves every explorer's selection.
  useEffect(() => {
    if (exists) store.getState().pruneSelection(exists);
  }, [exists, store]);
  return useMemo(
    () => inspectorContext({ workspace, explorer: { active, pinned }, selectionFrom, editors, generation, selectionBy, exists, inspectedTable }),
    [workspace, active, pinned, selectionFrom, editors, generation, selectionBy, exists, inspectedTable],
  );
}

export function Inspector({ context }: { context: InspectorContext }) {
  // The selected tab, remembered per kind while the inspector stays up (./tabs resolves it against the kind's tabs).
  const [tabs, setTabs] = useState<Record<string, string>>({});
  const onTab = useCallback((kind: ElementKind, tab: string) => setTabs((t) => (t[kind] === tab ? t : { ...t, [kind]: tab })), []);
  if (context.mode === "none") return null;
  if (context.mode === "pack") return <PackInspector key={context.pack} pack={context.pack} unit={context.unit} />;
  if (context.mode === "table") return <TableInspector database={context.database} tableKey={context.key} column={context.column} />;
  if (context.mode === "empty")
    return (
      <section
        aria-label="Inspector"
        className="flex h-full flex-col items-center justify-center gap-1 bg-surface p-2 text-center"
        data-testid="inspector-empty"
      >
        <h2 className="text-13 font-medium text-primary" data-testid="inspector-title">
          {emptyTitle(context)}
        </h2>
        <p className="max-w-md text-12 text-secondary">
          {context.noun === "a pack" ? "Its units and output folder show here." : "Its properties show here, ready to edit."}
        </p>
      </section>
    );
  if (context.ids.length > 1) return <BulkInspector ids={context.ids} />;
  return <ElementInspector key={context.ids[0]} id={context.ids[0]} tabs={tabs} onTab={onTab} />;
}

/** The Generate screen's context: the open pack, or the unit a tree row focused. */
function PackInspector({ pack, unit }: { pack: string; unit: string | null }) {
  const doc = usePack(pack);
  const units = ((doc.data?.document?.units as Record<string, unknown>[] | undefined) ?? []).filter((u) => typeof u.id === "string");
  const row = unit ? units.find((u) => u.id === unit) : undefined;
  const title = row ? `${pack}/${unit}` : pack;
  const text = (v: unknown) => (typeof v === "string" && v ? v : "none");
  return (
    <section aria-label={`Inspector: ${title}`} className="flex h-full min-h-0 flex-col bg-surface" data-testid="inspector">
      <header className="flex flex-col gap-0.5 border-b border-default px-2 py-1">
        <h2 className="truncate text-14 font-semibold" data-testid="inspector-title">
          {title}
        </h2>
        <p className="truncate font-mono text-11 text-secondary">{row ? "unit" : "pack"}</p>
      </header>
      {doc.isPending ? (
        <Spinner label="Loading pack" />
      ) : !doc.data ? (
        <EmptyState title="This pack could not be read">{doc.error ? String((doc.error as Error).message) : null}</EmptyState>
      ) : (
        <dl className="grid grid-cols-[max-content_1fr] gap-x-2 gap-y-0.5 overflow-auto p-2 text-12" data-testid="pack-inspector">
          {row ? (
            <>
              <dt className="text-secondary">Template</dt>
              <dd className="truncate font-mono">{text(row.template)}</dd>
              <dt className="text-secondary">Renders</dt>
              <dd className="truncate font-mono">{text(row.for)}</dd>
              <dt className="text-secondary">Output</dt>
              <dd className="break-all font-mono">{text(row.output)}</dd>
              <dt className="text-secondary">Mode</dt>
              <dd>{text(row.mode)}</dd>
            </>
          ) : (
            <>
              <dt className="text-secondary">Enabled</dt>
              <dd>{doc.data.enabled ? "yes" : "no"}</dd>
              <dt className="text-secondary">Output</dt>
              <dd className="break-all font-mono">{doc.data.output}</dd>
              <dt className="text-secondary">Units</dt>
              <dd>{units.map((u) => String(u.id)).join(", ") || "none"}</dd>
              <dt className="text-secondary">Files</dt>
              <dd>{doc.data.files.length}</dd>
              <dt className="text-secondary">Problems</dt>
              <dd>{doc.data.diagnostics.length}</dd>
            </>
          )}
        </dl>
      )}
    </section>
  );
}

export function statusBadge(status: string | undefined, saving: boolean) {
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

function ElementInspector({ id, tabs, onTab }: { id: string; tabs: Record<string, string>; onTab: (kind: ElementKind, tab: string) => void }) {
  const { element, draft, json, edit, flush } = useDraftDocument(id);
  const project = useProject();
  const index = useIndex();
  const { store } = useServices();
  const focus = useEditor(store, (s) => s.focus);
  const inspectedTable = useEditor(store, (s) => s.inspectedTable);
  const kind = (json as { kind?: ElementKind } | undefined)?.kind;
  const tab = kind ? resolveInspectorTab(kind, tabs[kind]) : "properties";
  const vocab = useVocabularies(kind ?? "entity");
  const summary = index.data?.find((r) => r.id === id);
  // A state or transition selected in the process editor: the inspector shows it in place of the process's fields.
  const processNode = useProcessNodeShown(id, kind);

  useEffect(() => {
    if (!focus?.pointer || focus.id !== id) return;
    // Reveal the field a problem points at: grid cells carry data-cell, inputs their ids.
    const match = /^\/attributes\/(\d+)(?:\/(\w+))?/.exec(focus.pointer);
    // An attribute's problem shows on the Attributes tab where the grid lives there.
    const toTab = !!match && !!kind && attributesView(kind) === "grid";
    if (toTab) onTab(kind, "attributes");
    const show = () => {
      const target = match
        ? document.querySelector<HTMLElement>(`[data-testid="attribute-grid"] [data-cell^="${match[1]}:"][data-column="${match[2] ?? "name"}"]`)
        : document.getElementById(`${id}-${focus.pointer!.split("/")[1]}`);
      target?.scrollIntoView({ block: "nearest" });
      target?.focus();
    };
    // A tab switch mounts the grid a frame later.
    requestAnimationFrame(() => (toTab ? requestAnimationFrame(show) : show()));
  }, [focus, id, kind, onTab]);

  // The index is loaded and has no row: the element was deleted; its cached document is never shown or edited.
  if (index.data && !summary) return <EmptyState title="This element no longer exists">It was deleted.</EmptyState>;
  if (element.isPending && !json) return <Spinner label="Loading element" />;
  if (element.error && !json) return <EmptyState title="This element could not be loaded">{String((element.error as Error).message)}</EmptyState>;
  if (!json || !kind) return null;
  // A table of its own (designed, imported) shows as the table inspector does on the Databases side: its physical fields and
  // the column picked in its editor's grid.
  const tableDoc = json as { origin?: string; database?: unknown };
  if (kind === "table" && tableDoc.origin !== "synthesized" && typeof tableDoc.database === "string") {
    const database = tableDoc.database;
    const column = inspectedTable?.database === database && inspectedTable.key === id ? inspectedTable.column : null;
    return <TableInspector database={database} tableKey={id} column={column} />;
  }

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
      <header className="flex flex-col gap-1 border-b border-default px-2 py-1">
        <div className="flex items-center gap-2">
          <KindIcon kind={kind} />
          <h2 className="min-w-0 flex-1 truncate text-14 font-semibold" data-testid="inspector-title">
            {String((json as { name?: string }).name ?? "") || displayName(summary)}
          </h2>
          <span data-testid="save-status">{statusBadge(draft?.status, false)}</span>
          {kind === "table" ? <OpenTableButton json={json} /> : null}
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
      <Tabs value={tab} onValueChange={(next) => onTab(kind, next)} className="flex min-h-0 flex-1 flex-col">
        <TabsList aria-label="Inspector views">
          {inspectorTabs(kind).map((t) => (
            <TabsTrigger key={t} value={t}>
              {INSPECTOR_TAB_LABELS[t]}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="properties" className="overflow-auto p-2">
          {processNode ? (
            <ProcessInspectorSection {...props} />
          ) : (
            <div className="flex flex-col gap-2">
              <CommonFields {...props} />
              <TranslationsSection id={id} kind={kind} />
              {kind === "entity" ? <EntityFields {...props} /> : null}
              {kind === "relation" ? <RelationFields {...props} withAttributes={false} /> : null}
              {kind === "enum" ? <EnumFields {...props} /> : null}
              {kind === "scalar-type" ? <ScalarFields {...props} /> : null}
              {kind === "database" ? <DatabaseFields {...props} /> : null}
              {kind === "view" ? <ViewFields {...props} /> : null}
              {kind === "sequence" ? (
                <>
                  <SchemaField {...props} />
                  <SequenceFields {...props} />
                </>
              ) : null}
              {kind === "routine" ? <RoutineFields {...props} /> : null}
              {kind === "database-type" ? <DatabaseTypeFields {...props} /> : null}
              {kind === "sql-object" ? <SqlObjectFields {...props} /> : null}
              {kind === "process" ? <ProcessInspectorSection {...props} /> : null}
              {kind === "actor" ? <ActorInspectorSection {...props} /> : null}
              {kind === "scenario" ? <ScenarioInspectorSection {...props} /> : null}
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
              {![
                "entity",
                "relation",
                "value-object",
                "stereotype",
                "enum",
                "scalar-type",
                "database",
                "view",
                "sequence",
                "routine",
                "database-type",
                "sql-object",
                "package",
                "diagram",
                "process",
                "actor",
                "scenario",
              ].includes(kind) ? (
                <p className="text-12 text-secondary">Every other property of this {KIND_LABELS[kind].toLowerCase()} is editable in the JSON view.</p>
              ) : null}
            </div>
          )}
        </TabsContent>
        {attributesView(kind) ? (
          <TabsContent value="attributes" className="overflow-auto p-2">
            {attributesView(kind) === "grid" ? <AttributesOnlyFields {...props} withTexts={false} /> : <EntityAttributeList id={id} json={json} />}
          </TabsContent>
        ) : null}
        <TabsContent value="json" className="flex min-h-0 flex-col p-0">
          <JsonTab json={json} onChange={(next) => edit(() => next)} />
        </TabsContent>
        <TabsContent value="references" className="overflow-auto p-2">
          <References id={id} />
        </TabsContent>
      </Tabs>
    </section>
  );
}

export function JsonTab({ json, onChange }: { json: ModelJson; onChange: (json: ModelJson) => void }) {
  const [error, setError] = useState<string | null>(null);
  const text = useMemo(() => JSON.stringify(json, null, 2), [json]);
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {error ? (
        <p role="alert" className="border-b border-default px-2 py-1 text-12 text-danger">
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

export function References({ id }: { id: string }) {
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

/**
 * Delete: reads both delete plans first. With nothing else involved it deletes at once (one undo step, as before);
 * otherwise the dialog lists what each way through does and offers "Delete and clear references" and "Delete with
 * N dependents". A refused delete closes the dialog and says why in the engine's readable words.
 */
export function DeleteButton({ id, name }: { id: string; name: string }) {
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const { select } = useEditorNavigation();
  const [plans, setPlans] = useState<DeletePlans | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (resolution: DeleteResolution, plan: DeletePlan) => {
    setPlans(null);
    setBusy(true);
    try {
      const label =
        resolution === "delete-dependents"
          ? `Delete ${name} with dependents`
          : resolution === "remove-references"
            ? `Delete ${name} and clear references`
            : `Delete ${name}`;
      const reason = await runPlannedDelete({ queryClient: qc, drafts, store }, { ids: [id], resolution, plan, label });
      if (reason) store.getState().notify(`${name} was not deleted: ${reason}`, "error");
      else select([]);
    } finally {
      setBusy(false);
    }
  };
  const start = async () => {
    setBusy(true);
    try {
      await drafts.flush(id);
      const loaded = await loadDeletePlans([id]);
      if (deletesAlone(loaded)) await run("refuse", loaded.dependents);
      else setPlans(loaded);
    } catch (e) {
      store.getState().notify(`${name} was not deleted: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <Button size="icon-sm" variant="ghost" label={`Delete ${name}`} disabled={busy} onClick={() => void start()}>
        <Trash2 />
      </Button>
      <DeletePlanDialog
        ids={plans ? [id] : null}
        title={`Delete ${name}?`}
        preloaded={plans}
        onClose={() => setPlans(null)}
        onDelete={(r, p) => void run(r, p)}
      />
    </>
  );
}

/** Multi-select: bulk edits as one batch (apply a stereotype or a tag to many elements). */
function BulkInspector({ ids }: { ids: string[] }) {
  useServices();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const rows = ids.map((id) => lookup.byId.get(id)).filter((r): r is NonNullable<typeof r> => !!r);
  const kinds = [...new Set(rows.map((r) => r.kind))];
  const domain = commonDomain(
    rows.map((r) => (r.kind === "package" ? r.id : r.package)),
    index.data ?? [],
  );
  const vocab = useVocabularies(kinds.length === 1 ? kinds[0] : "entity", domain);
  const [stereotype, setStereotype] = useState("");
  const [tag, setTag] = useState("");
  const [busy, setBusy] = useState(false);

  const batchEdit = useBatchEdit();
  const apply = async (label: string, mutate: (json: ModelJson) => void) => {
    setBusy(true);
    try {
      await batchEdit(label, ids, (json) => mutate(json as unknown as ModelJson));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-label="Inspector: bulk edit" className="flex h-full flex-col gap-2 bg-surface p-2" data-testid="bulk-inspector">
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
