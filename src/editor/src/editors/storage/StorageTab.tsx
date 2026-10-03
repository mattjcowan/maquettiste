// The entity editor's Storage tab (erratum E43; the owner: "do the mapping on the entity side completely"): one card per
// binding (one per database) with its source, constants, field map, the source's columns with what accounts for each, write
// and delete, the five statements and the binding's problems; Add binding, and the bulk actions over this entity. An entity
// with no binding is domain only. Every gesture is one save of the entity and one undo step.
import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CircleAlert, Plus, Trash2, TriangleAlert, X } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, useIndex, useValidation } from "@/api/queries";
import type { ElementSummary, EntityDoc } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { newId } from "@/lib/ids";
import { indexLookup } from "@/model/index";
import { relatedOf } from "@/editors/related";
import type { EditorContext } from "@/editors/EditorFrame";
import {
  applyMatches,
  attributeRows,
  bindingProblems,
  COLUMN_STATUS_LABELS,
  columnRows,
  constantValue,
  findColumn,
  matchByName,
  newBinding,
  retarget,
  setColumnStatus,
  setConstants,
  setDelete,
  setField,
  setWrite,
  tableForEntity,
  writeChoiceOf,
  writes,
  type Binding,
  type BindingSource,
  type ColumnStatus,
  type ListedStatus,
  type MapProblem,
  type StorageAttribute,
} from "./fieldMap";
import { databasesOf, useBindingSources, useMaterializeStatuses, useStorageAttributes } from "./useStorage";
import { STORAGE_LABELS } from "./labels";

type Rec = Record<string, unknown>;

const DIALECTS = ["postgresql", "sqlserver", "mysql", "sqlite", "oracle"] as const;
const STATEMENTS = [
  ["select", "Select"],
  ["selectByKey", "Select by key"],
  ["insert", "Insert"],
  ["update", "Update"],
  ["delete", "Delete"],
] as const;

export function StorageTab(ctx: EditorContext) {
  const { id, json } = ctx;
  const entity = json as EntityDoc;
  const bindings = (entity.bindings ?? []) as Binding[];
  const index = useIndex();
  const { store } = useServices();
  const lookup = indexLookup(index.data);
  const overrides = useMemo(() => new Map([[id, json as Rec]]), [id, json]);
  const ids = useMemo(() => [id], [id]);
  const { attributes } = useStorageAttributes(ids, overrides);
  const attrs = attributes.get(id) ?? [];
  const databases = databasesOf(index.data);
  const mappings = relatedOf(index.data, id).mappings;
  const unbound = databases.filter((d) => !bindings.some((b) => b.database === d.id));
  const statuses = useMaterializeStatuses(
    unbound.map((d) => d.id),
    bindings.length === 0 || mappings.length > 0,
  );
  const projectedIn = unbound.filter((d) => statuses.statuses.some((s) => s.database === d.id && s.entities.some((e) => e.id === id && e.projected)));
  const edit = (i: number, change: (b: Binding) => void) => {
    ctx.edit((j) => {
      const list = ((j as Rec).bindings as Binding[] | undefined) ?? [];
      if (list[i]) change(list[i]);
    });
    ctx.flush();
  };
  const remove = (i: number) => {
    ctx.edit((j) => {
      const list = (((j as Rec).bindings as Binding[] | undefined) ?? []).filter((_, k) => k !== i);
      if (list.length) (j as Rec).bindings = list;
      else delete (j as Rec).bindings;
    });
    ctx.flush();
  };
  const add = (binding: Binding) => {
    ctx.edit((j) => {
      (j as Rec).bindings = [...(((j as Rec).bindings as Binding[] | undefined) ?? []), binding];
    });
    ctx.flush();
  };
  const ask = (action: "auto-map" | "create-tables") => store.getState().requestStorage({ action, entities: [id], domain: entity.package ?? null });
  return (
    <div className="flex flex-col gap-2" data-testid="storage-tab">
      <div className="flex flex-wrap items-center gap-1">
        <AddBinding entityName={entity.name} databases={unbound} attributes={attrs} onAdd={add} />
        <Button size="sm" onClick={() => ask("auto-map")} data-testid="storage-auto-map">
          {STORAGE_LABELS.autoMap}
        </Button>
        <Button size="sm" onClick={() => ask("create-tables")} data-testid="storage-create-table" disabled={!unbound.length}>
          {STORAGE_LABELS.createTableForEntity}
        </Button>
      </div>
      {bindings.length === 0 ? (
        <div className="rounded-control border border-default bg-app p-2 text-13" data-testid="storage-domain-only">
          <p className="font-medium">{STORAGE_LABELS.domainOnly}</p>
          <p className="text-12 text-secondary">
            Add a binding to read and write it through a table, a view or a query, or create a table for it.
            {projectedIn.length
              ? ` ${projectedIn.map((d) => d.name).join(", ")} still ${projectedIn.length === 1 ? "projects" : "project"} a table for it by convention (an older format); Create a table for this entity… makes that explicit.`
              : ""}
          </p>
        </div>
      ) : null}
      {mappings.map((m) => (
        <LegacyMappingNote
          key={m.id}
          mapping={m}
          databaseName={lookup.nameOf(m.database ?? "") ?? "a database"}
          bound={bindings.some((b) => b.database === m.database)}
        />
      ))}
      {bindings.map((b, i) => (
        <BindingCard
          key={b.id}
          entityId={id}
          entityName={entity.name}
          index={i}
          binding={b}
          databaseName={lookup.nameOf(b.database) ?? b.database}
          attributes={attrs}
          onEdit={(change) => edit(i, change)}
          onRemove={() => remove(i)}
        />
      ))}
    </div>
  );
}

// ------------------------------------------------------------------ add a binding

function AddBinding({
  entityName,
  databases,
  attributes,
  onAdd,
}: {
  entityName: string;
  databases: ElementSummary[];
  attributes: StorageAttribute[];
  onAdd: (binding: Binding) => void;
}) {
  const [open, setOpen] = useState(false);
  const [database, setDatabase] = useState("");
  const db = database || databases[0]?.id || "";
  const all = useBindingSources(open ? db : null);
  // A projected table is left out: binding to it would end the projection that makes it (Create a table… is for that).
  const sources = all.sources.filter((s) => !s.projected);
  const pending = all.pending;
  const guess = tableForEntity(
    entityName,
    sources.filter((s) => s.kind === "table"),
  );
  const [picked, setPicked] = useState<string | null>(null);
  const source = sources.find((s) => s.id === (picked ?? guess?.id)) ?? null;
  return (
    <>
      <Button
        size="sm"
        onClick={() => setOpen(true)}
        disabled={!databases.length}
        data-testid="storage-add-binding"
        title={databases.length ? undefined : "Every database already binds this entity."}
      >
        <Plus /> Add binding…
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent
          title={`Add a binding to ${entityName}`}
          description="Where the entity reads its rows: a table, a view or a query of a database. Fields are mapped by name to start with."
        >
          <div className="flex flex-col gap-2" data-testid="add-binding-dialog">
            <Field label="Database" htmlFor="add-binding-database">
              <Select
                id="add-binding-database"
                value={db}
                onChange={(e) => {
                  setDatabase(e.target.value);
                  setPicked(null);
                }}
              >
                {databases.map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Source" htmlFor="add-binding-source" hint={pending ? "Loading the database…" : undefined}>
              <SourceSelect
                id="add-binding-source"
                sources={sources}
                value={source?.id ?? ""}
                onChange={(v) => setPicked(v)}
                placeholder="Pick a table, view or query"
              />
            </Field>
            <div className="flex justify-end gap-2">
              <Button onClick={() => setOpen(false)}>Cancel</Button>
              <Button
                variant="primary"
                disabled={!source}
                data-testid="add-binding-apply"
                onClick={() => {
                  if (!source) return;
                  onAdd(newBinding(newId(), db, source, attributes));
                  setOpen(false);
                  setPicked(null);
                }}
              >
                Add binding
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}

function SourceSelect({
  id,
  sources,
  value,
  onChange,
  placeholder,
  ...rest
}: {
  id?: string;
  sources: BindingSource[];
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  "aria-label"?: string;
  "data-testid"?: string;
  className?: string;
}) {
  const group = (kind: BindingSource["kind"], label: string) => {
    const list = sources.filter((s) => s.kind === kind);
    return list.length ? (
      <optgroup label={label}>
        {list.map((s) => (
          <option key={s.id} value={s.id}>
            {s.schema ? `${s.schema}.${s.name}` : s.name}
            {s.projected ? " (projected)" : ""}
          </option>
        ))}
      </optgroup>
    ) : null;
  };
  return (
    <Select id={id} value={value} onChange={(e) => onChange(e.target.value)} {...rest}>
      {!value || !sources.some((s) => s.id === value) ? <option value={value}>{value ? `${value} (not found)` : (placeholder ?? "")}</option> : null}
      {group("table", "Tables")}
      {group("view", "Views")}
      {group("query", "Queries")}
    </Select>
  );
}

// ------------------------------------------------------------------ the legacy mapping element

function LegacyMappingNote({ mapping, databaseName, bound }: { mapping: ElementSummary; databaseName: string; bound: boolean }) {
  const services = useServices();
  const { openEditor } = useEditorNavigation();
  const [busy, setBusy] = useState(false);
  return (
    <div className="flex items-center gap-2 rounded-control border border-default p-1 text-12" data-testid="storage-legacy-mapping">
      <TriangleAlert className="size-3.5 shrink-0 text-warning" aria-hidden />
      <span className="min-w-0 flex-1">
        {bound
          ? `This entity also has an older mapping element for ${databaseName}; its binding wins.`
          : `An older mapping element places this entity in ${databaseName} by convention.`}
      </span>
      <Button size="sm" variant="ghost" onClick={() => openEditor(mapping, true)}>
        Open
      </Button>
      <Button
        size="sm"
        variant="ghost"
        disabled={busy}
        data-testid="storage-legacy-mapping-delete"
        onClick={async () => {
          setBusy(true);
          const failed = await deleteWithUndo(services, mapping.id, `Delete ${mapping.name}`);
          setBusy(false);
          if (failed) services.store.getState().notify(failed, "error");
        }}
      >
        Delete
      </Button>
    </div>
  );
}

async function deleteWithUndo(services: ReturnType<typeof useServices>, id: string, label: string): Promise<string | null> {
  const doc = await endpoints.getElement(id);
  const result = await endpoints.applyBatch({ operations: [{ op: "delete", id, expectedHash: doc.hash }] } as never);
  if (!endpoints.isBatchResult(result) || result.outcome !== "saved") return `${label} failed.`;
  applyBatchResult(services.queryClient, result);
  services.store.getState().pushUndo({ label, ids: [id], before: [doc.json], after: [null], afterHashes: [null] });
  return null;
}

// ------------------------------------------------------------------ one binding

function BindingCard({
  entityId,
  entityName,
  index,
  binding,
  databaseName,
  attributes,
  onEdit,
  onRemove,
}: {
  entityId: string;
  entityName: string;
  index: number;
  binding: Binding;
  databaseName: string;
  attributes: StorageAttribute[];
  onEdit: (change: (b: Binding) => void) => void;
  onRemove: () => void;
}) {
  const { sources, pending, stale } = useBindingSources(binding.database, binding.source);
  const source = sources.find((s) => s.id === binding.source);
  const columns = source?.columns ?? [];
  const fieldRows = attributeRows(binding, attributes, columns);
  const colRows = columnRows(binding, attributes, columns);
  const validation = useValidation();
  const local = source || !pending ? bindingProblems(binding, attributes, source) : [];
  const localRules = new Set(local.map((p) => p.rule));
  const server: MapProblem[] = (validation.data?.diagnostics ?? [])
    .filter((d) => d.elementId === entityId && (d.jsonPointer ?? "").startsWith(`/bindings/${index}`) && !localRules.has(d.rule) && d.severity !== "info")
    .map((d) => ({ rule: d.rule, severity: d.severity === "error" ? "error" : "warning", message: d.message, pointer: d.jsonPointer ?? "" }));
  const problems = [...local, ...server];
  const [mapping, setMapping] = useState(false);
  const write = writeChoiceOf(binding);
  const writing = writes(binding, source);
  const tables = sources.filter((s) => s.kind === "table");
  const del = binding.delete ?? (writing ? "key" : "none");
  const deleteMode = typeof del === "object" ? "soft" : del;
  const dom = `binding-${binding.id}`;
  return (
    <section
      className="flex flex-col gap-2 rounded-control border border-default p-2"
      data-testid="binding-card"
      data-database={databaseName}
      aria-label={`Binding to ${databaseName}`}
    >
      <div className="flex flex-wrap items-end gap-2">
        <h3 className="text-13 font-semibold">{databaseName}</h3>
        <Field label="Source" htmlFor={`${dom}-source`} className="w-72">
          <SourceSelect
            id={`${dom}-source`}
            sources={sources.filter((s) => !s.projected || s.id === binding.source)}
            value={binding.source}
            data-testid="binding-source"
            onChange={(v) => {
              const next = sources.find((s) => s.id === v);
              if (next) onEdit((b) => retarget(b, next));
            }}
          />
        </Field>
        <span className="text-12 text-secondary">
          {pending
            ? "Loading the database…"
            : stale
              ? "The model has errors: the columns are from its last valid state."
              : source
                ? `${source.kind}, ${columns.length} columns`
                : ""}
        </span>
        <span className="flex-1" />
        <Button
          size="icon-sm"
          variant="ghost"
          label={`Remove the binding to ${databaseName} (the source stays)`}
          onClick={onRemove}
          data-testid="binding-remove"
        >
          <Trash2 />
        </Button>
      </div>

      {problems.length ? (
        <ul className="flex flex-col gap-0.5 text-12" data-testid="binding-problems">
          {problems.map((p, i) => (
            <li key={i} className={cn("flex gap-1", p.severity === "error" ? "text-danger" : "text-warning")} data-rule={p.rule}>
              {p.severity === "error" ? (
                <CircleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
              ) : (
                <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
              )}
              <span>
                <span className="font-mono">{p.rule}</span> {p.message}
              </span>
            </li>
          ))}
        </ul>
      ) : null}

      <ConstantsGrid dom={dom} binding={binding} columns={columns} onEdit={onEdit} />

      <section className="flex flex-col gap-1">
        <SectionTitle
          actions={
            <Button size="sm" variant="ghost" onClick={() => setMapping(true)} disabled={!source} data-testid="binding-map-by-name">
              Map by name…
            </Button>
          }
        >
          Field map
        </SectionTitle>
        <table className="w-full text-13" aria-label={`Fields of ${entityName} in ${databaseName}`} data-testid="binding-fields">
          <thead className="text-left text-11 text-secondary">
            <tr>
              <th className="font-medium">Attribute</th>
              <th className="font-medium">From</th>
              <th className="font-medium">Column</th>
              <th className="font-medium">Status</th>
            </tr>
          </thead>
          <tbody>
            {fieldRows.map((r) => (
              <tr key={r.attribute.ref} data-testid={`field-row-${r.attribute.name}`} data-status={r.status}>
                <td className="font-mono">
                  {r.attribute.name}
                  {r.attribute.isKey ? <span className="ml-1 rounded-[4px] bg-app px-1 font-sans text-11">key</span> : null}
                </td>
                <td className="text-12 text-secondary">{r.attribute.from ?? (r.attribute.origin === "own" ? "" : r.attribute.origin)}</td>
                <td className="w-56 py-0.5">
                  <Select
                    aria-label={`Column of ${r.attribute.name}`}
                    className="h-6"
                    value={r.column?.key ?? (r.field ? r.field.column : "")}
                    onChange={(e) => {
                      const column = columns.find((c) => c.key === e.target.value) ?? null;
                      onEdit((b) => setField(b, r.attribute.ref, column, columns));
                    }}
                  >
                    <option value="">(not mapped)</option>
                    {r.field && !r.column ? <option value={r.field.column}>{`${r.field.column} (not found)`}</option> : null}
                    {columns.map((c) => (
                      <option key={c.key} value={c.key}>
                        {c.name}
                      </option>
                    ))}
                  </Select>
                </td>
                <td className={cn("text-12", r.status === "missing-column" ? "text-danger" : r.status === "unmapped" ? "text-warning" : "text-secondary")}>
                  {{ mapped: "mapped", "missing-column": "no such column", unmapped: "not mapped", optional: "not mapped (optional)" }[r.status]}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section className="flex flex-col gap-1">
        <SectionTitle>Columns of {source?.name ?? "the source"}</SectionTitle>
        <table className="w-full text-13" aria-label={`Columns of ${source?.name ?? "the source"}`} data-testid="binding-columns">
          <thead className="text-left text-11 text-secondary">
            <tr>
              <th className="font-medium">Column</th>
              <th className="font-medium">Type</th>
              <th className="font-medium">Status</th>
              <th className="font-medium">Account for</th>
            </tr>
          </thead>
          <tbody>
            {colRows.map((r) => {
              const listed = (binding.columns ?? []).find((c) => findColumn(columns, c.column) === r.column);
              const free = r.status !== "field" && r.status !== "constant" && r.status !== "soft-delete";
              return (
                <tr key={r.column.key} data-testid={`column-row-${r.column.name}`} data-status={r.status}>
                  <td className="font-mono">{r.column.name}</td>
                  <td className="text-12 text-secondary">
                    {r.column.type ?? ""}
                    {r.column.nullable ? "" : " not null"}
                  </td>
                  <td className={cn("text-12", r.status === "unaccounted" ? "text-warning" : "text-secondary")}>
                    {statusText(r.status, r.attribute?.name ?? r.fieldRef, r.constant?.value)}
                  </td>
                  <td className="py-0.5">
                    {free ? (
                      <div className="flex items-center gap-1">
                        {r.status === "unaccounted" ? (
                          <Button
                            size="sm"
                            variant="ghost"
                            data-testid="column-ignore"
                            onClick={() => onEdit((b) => setColumnStatus(b, r.column, "ignored", columns))}
                          >
                            Ignore
                          </Button>
                        ) : null}
                        <Select
                          aria-label={`What accounts for ${r.column.name}`}
                          className="h-6 w-44"
                          value={listed?.status ?? ""}
                          onChange={(e) => onEdit((b) => setColumnStatus(b, r.column, (e.target.value || null) as ListedStatus | null, columns))}
                        >
                          <option value="">(its own: {r.status === "unaccounted" ? "nothing" : COLUMN_STATUS_LABELS[r.status]})</option>
                          <option value="ignored">Ignored</option>
                          <option value="database">Filled by the database</option>
                          <option value="computed">Computed</option>
                        </Select>
                      </div>
                    ) : null}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </section>

      <div className="flex flex-wrap items-end gap-2">
        <Field label="Write" htmlFor={`${dom}-write`} className="w-64">
          <Select
            id={`${dom}-write`}
            data-testid="binding-write"
            value={typeof write === "object" ? write.table : write}
            onChange={(e) => {
              const v = e.target.value;
              onEdit((b) => setWrite(b, v === "source" || v === "none" ? v : { table: v }));
            }}
          >
            <option value="source">{source?.kind === "table" || !source ? "The source table" : "None (the source is not a table)"}</option>
            <option value="none">None (read-only)</option>
            <optgroup label="Another table">
              {tables
                .filter((t) => t.id !== binding.source)
                .map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
            </optgroup>
          </Select>
        </Field>
        <Field label="Delete" htmlFor={`${dom}-delete`} className="w-48">
          <Select
            id={`${dom}-delete`}
            data-testid="binding-delete"
            value={deleteMode}
            disabled={!writing}
            title={writing ? undefined : "A read-only binding deletes nothing."}
            onChange={(e) => {
              const v = e.target.value;
              const soft = columns.find((c) => c.name.toLowerCase().includes("deleted")) ?? columns.find((c) => !c.isPrimaryKey);
              onEdit((b) => setDelete(b, v === "soft" ? { soft: { column: soft?.key ?? "", value: true } } : (v as "key" | "none")));
            }}
          >
            <option value="key">By key</option>
            <option value="soft">Soft delete (set a column)</option>
            <option value="none">None</option>
          </Select>
        </Field>
        {typeof del === "object" ? (
          <>
            <Field label="Soft-delete column" htmlFor={`${dom}-soft-column`} className="w-48">
              <Select
                id={`${dom}-soft-column`}
                value={findColumn(columns, del.soft.column)?.key ?? del.soft.column}
                onChange={(e) => onEdit((b) => setDelete(b, { soft: { ...del.soft, column: e.target.value } }))}
              >
                {columns.map((c) => (
                  <option key={c.key} value={c.key}>
                    {c.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Value set" htmlFor={`${dom}-soft-value`} className="w-32">
              <Input
                id={`${dom}-soft-value`}
                className="font-mono"
                key={String(del.soft.value ?? "")}
                defaultValue={del.soft.value === undefined || del.soft.value === null ? "" : String(del.soft.value)}
                onBlur={(e) => {
                  const value = constantValue(e.target.value);
                  if (value !== (del.soft.value ?? null))
                    onEdit((b) => setDelete(b, { soft: { column: del.soft.column, ...(value === null ? {} : { value }) } }));
                }}
              />
            </Field>
          </>
        ) : null}
      </div>

      <SqlDisclosure entityId={entityId} bindingId={binding.id} />

      {mapping && source ? (
        <MapByNameDialog
          binding={binding}
          attributes={attributes}
          source={source}
          onClose={() => setMapping(false)}
          onApply={(refs) => {
            const matches = matchByName(binding, attributes, columns).filter((m) => refs.includes(m.attribute.ref));
            if (matches.length) onEdit((b) => applyMatches(b, matches, columns));
            setMapping(false);
          }}
        />
      ) : null}
    </section>
  );
}

function statusText(status: ColumnStatus, attribute: string | undefined, value: unknown): string {
  if (status === "field") return `mapped by ${attribute ?? "?"}`;
  if (status === "constant") return `constant ${value === undefined || value === null ? "NULL" : JSON.stringify(value)}`;
  return COLUMN_STATUS_LABELS[status];
}

// ------------------------------------------------------------------ constants

function ConstantsGrid({
  dom,
  binding,
  columns,
  onEdit,
}: {
  dom: string;
  binding: Binding;
  columns: BindingSource["columns"];
  onEdit: (change: (b: Binding) => void) => void;
}) {
  const constants = binding.constants ?? [];
  const set = (next: typeof constants) => onEdit((b) => setConstants(b, next));
  const free = columns.filter(
    (c) => !constants.some((k) => findColumn(columns, k.column) === c) && !(binding.fields ?? []).some((f) => findColumn(columns, f.column) === c),
  );
  return (
    <section className="flex flex-col gap-1">
      <SectionTitle
        actions={
          <Button
            size="sm"
            variant="ghost"
            disabled={!free.length}
            data-testid="binding-add-constant"
            onClick={() => set([...constants, { column: free[0].key, value: "" }])}
          >
            <Plus /> Add constant
          </Button>
        }
      >
        Constants
      </SectionTitle>
      {constants.length ? (
        <table className="w-full text-13" aria-label="Constant columns" data-testid="binding-constants">
          <thead className="text-left text-11 text-secondary">
            <tr>
              <th className="font-medium">Column</th>
              <th className="font-medium">Value</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {constants.map((k, i) => (
              <tr key={`${k.column}:${i}`} data-testid="constant-row">
                <td className="w-56 py-0.5">
                  <Select
                    aria-label={`Column of constant ${i + 1}`}
                    className="h-6"
                    value={findColumn(columns, k.column)?.key ?? k.column}
                    onChange={(e) => set(constants.map((x, j) => (j === i ? { ...x, column: e.target.value } : x)))}
                  >
                    {!findColumn(columns, k.column) ? <option value={k.column}>{`${k.column} (not found)`}</option> : null}
                    {columns.map((c) => (
                      <option key={c.key} value={c.key}>
                        {c.name}
                      </option>
                    ))}
                  </Select>
                </td>
                <td className="py-0.5">
                  <Input
                    id={`${dom}-constant-${i}`}
                    aria-label={`Value of constant ${i + 1}`}
                    className="h-6 font-mono"
                    placeholder="NULL"
                    key={`${i}:${String(k.value ?? "")}`}
                    defaultValue={k.value === undefined || k.value === null ? "" : String(k.value)}
                    onBlur={(e) => {
                      const value = constantValue(e.target.value);
                      if (value !== (k.value ?? null)) set(constants.map((x, j) => (j === i ? { column: x.column, value } : x)));
                    }}
                    onKeyDown={(e) => {
                      if (e.key === "Enter") (e.target as HTMLInputElement).blur();
                    }}
                  />
                </td>
                <td className="w-6">
                  <Button
                    size="icon-row"
                    variant="ghost"
                    label={`Remove the constant on ${findColumn(columns, k.column)?.name ?? k.column}`}
                    onClick={() => set(constants.filter((_, j) => j !== i))}
                  >
                    <X />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p className="text-12 text-secondary">None: every row of the source is this entity's.</p>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ Map by name

function MapByNameDialog({
  binding,
  attributes,
  source,
  onClose,
  onApply,
}: {
  binding: Binding;
  attributes: StorageAttribute[];
  source: BindingSource;
  onClose: () => void;
  onApply: (refs: string[]) => void;
}) {
  const matches = matchByName(binding, attributes, source.columns);
  const [off, setOff] = useState<Set<string>>(new Set());
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent
        title="Map by name"
        description={`Attributes without a field, matched to the columns of ${source.name} with the same name (case and underscores ignored).`}
      >
        <div className="flex flex-col gap-2" data-testid="map-by-name-dialog">
          {matches.length ? (
            <ul className="flex max-h-[50vh] flex-col gap-0.5 overflow-auto">
              {matches.map((m) => (
                <li key={m.attribute.ref}>
                  <CheckboxField
                    id={`map-by-name-${m.attribute.ref}`}
                    label={`${m.attribute.name} → ${m.column.name}`}
                    checked={!off.has(m.attribute.ref)}
                    onChange={(on) => {
                      const next = new Set(off);
                      if (on) next.delete(m.attribute.ref);
                      else next.add(m.attribute.ref);
                      setOff(next);
                    }}
                  />
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-12 text-secondary">No unmapped attribute has a column of the same name.</p>
          )}
          <div className="flex justify-end gap-2">
            <Button onClick={onClose}>Cancel</Button>
            <Button
              variant="primary"
              disabled={!matches.some((m) => !off.has(m.attribute.ref))}
              data-testid="map-by-name-apply"
              onClick={() => onApply(matches.filter((m) => !off.has(m.attribute.ref)).map((m) => m.attribute.ref))}
            >
              Map {matches.filter((m) => !off.has(m.attribute.ref)).length}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

// ------------------------------------------------------------------ SQL

function SqlDisclosure({ entityId, bindingId }: { entityId: string; bindingId: string }) {
  const [open, setOpen] = useState(false);
  const [dialect, setDialect] = useState("");
  const sql = useQuery({
    queryKey: ["preview", "binding-sql", entityId, bindingId, dialect],
    queryFn: () => endpoints.getBindingSql(entityId, bindingId, dialect || null),
    enabled: open,
  });
  return (
    <details className="text-12" onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)} data-testid="binding-sql">
      <summary className="cursor-pointer select-none font-medium text-secondary">SQL</summary>
      <div className="mt-1 flex flex-col gap-1">
        <Select aria-label="Dialect" className="h-6 w-48" value={dialect} onChange={(e) => setDialect(e.target.value)}>
          <option value="">The database's dialect</option>
          {DIALECTS.map((d) => (
            <option key={d} value={d}>
              {d}
            </option>
          ))}
        </Select>
        {sql.isPending ? <p className="text-secondary">Rendering…</p> : null}
        {sql.data && !sql.data.preview ? (
          <ul className="text-danger">
            {sql.data.diagnostics.slice(0, 5).map((d, i) => (
              <li key={i}>
                <span className="font-mono">{d.rule}</span> {d.message}
              </li>
            ))}
          </ul>
        ) : null}
        {sql.data?.preview
          ? STATEMENTS.map(([key, label]) => {
              const statement = sql.data.preview![key];
              return (
                <div key={key} data-testid={`binding-sql-${key}`}>
                  <p className="font-medium text-secondary">{label}</p>
                  {statement ? (
                    <pre className="overflow-auto rounded-control bg-app p-1 font-mono text-12">{statement.sql}</pre>
                  ) : (
                    <p className="text-secondary">
                      None: the binding does not {key === "insert" || key === "update" ? "write" : key === "delete" ? "delete" : "read by key"}.
                    </p>
                  )}
                </div>
              );
            })
          : null}
      </div>
    </details>
  );
}
