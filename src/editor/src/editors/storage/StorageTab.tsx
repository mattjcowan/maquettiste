// The entity editor's Storage tab (erratum E43; the owner: "do the mapping on the entity side completely"): one card per
// binding (one per database) with its source, constants, field map, the source's columns with what accounts for each, write
// and delete, the five statements and the binding's problems; Add binding, and the bulk actions over this entity. An entity
// with no binding is domain only. Every gesture is one save of the entity and one undo step.
import { useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { useElements, useIndex, useValidation } from "@/api/queries";
import type { ElementSummary, EntityDoc } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { newId } from "@/lib/ids";
import { indexLookup } from "@/model/index";
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
  setDelete,
  setField,
  setWrite,
  storedByChild,
  tableForEntity,
  typeColumnOf,
  writeChoiceOf,
  writes,
  type Binding,
  type BindingSource,
  type ColumnStatus,
  type ListedStatus,
  type MapProblem,
  type ProblemExtras,
  type StorageAttribute,
} from "./fieldMap";
export { ConstantsGrid } from "./ConstantsGrid";
import { ConstantsGrid } from "./ConstantsGrid";
import { baseProblems, boundBaseOf } from "./baseTable";
import { bindingShape } from "./bindingShape";
import { BaseTableSection, ChildTablesSection, Problems, StoreWholeAsJson } from "./StorageShapes";
import { plainTable, type TableDoc } from "./tableDoc";
import { commitMaterialize, databasesOf, useBindingSources, useRelationColumns, useStorageAttributes } from "./useStorage";
import { STORAGE_LABELS } from "./labels";
import { derivedOf, WALK_LIMITS } from "@/editors/refWalk";
import {
  codeValue,
  hostOf,
  isIntegerColumn,
  readCodes,
  retypeCode,
  sameCode,
  setTypeCode,
  setTypeColumn,
  typeColumnRole,
  type TypeColumnRole,
} from "./typeColumn";

type Rec = Record<string, unknown>;

/** The Column choice that adds a column for the attribute to the table (materialize-columns). */
const NEW_COLUMN = "\u0000new-column";

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
  const { attributes, docs } = useStorageAttributes(ids, overrides);
  const attrs = attributes.get(id) ?? [];
  const databases = databasesOf(index.data);
  const unbound = databases.filter((d) => !bindings.some((b) => b.database === d.id));
  // The entities derived from this one, found through what references each (one element at a time, never a scan of the index).
  const qc = useQueryClient();
  const derivedQuery = useQuery({
    // The index refreshes whenever an element is saved (a derived entity re-based, say), and the walk runs again then with fresh references.
    queryKey: ["derived-entities", id, index.dataUpdatedAt],
    queryFn: () => derivedOf(qc, id, (x) => lookup.byId.get(x)?.kind),
    enabled: lookup.byId.has(id),
  });
  const derived = useMemo(() => derivedQuery.data?.ids ?? [], [derivedQuery.data]);
  const derivedDocs = useElements(derived);
  // The documents the type column rules read: this entity and its bases (`docs`), with the derived entities over them.
  const hierarchyDocs = useMemo(() => {
    const all = new Map<string, Rec>(docs);
    for (const d of derivedDocs.byId.values()) all.set(String((d.json as Rec).id), d.json as Rec);
    return all;
  }, [docs, derivedDocs.byId]);
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
          <p className="text-12 text-secondary">Add a binding to read and write it through a table, a view or a query, or create a table for it.</p>
        </div>
      ) : null}
      {bindings.map((b, i) => (
        <BindingCard
          key={b.id}
          entityId={id}
          entityName={entity.name}
          index={i}
          binding={b}
          databaseName={lookup.nameOf(b.database) ?? b.database}
          attributes={attrs}
          typeColumn={typeColumnOf(id, b, docs)}
          typeRole={typeColumnRole(id, b, docs, derived.length > 0)}
          derived={derived}
          hierarchyDocs={hierarchyDocs}
          walkCut={derivedQuery.data?.truncated ?? false}
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
  const sources = all.sources;
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
            {source ? <ChangesDataNote source={source} /> : null}
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

/** The line under a source picker when the picked query changes data or returns no rows: an entity cannot read through it. */
function ChangesDataNote({ source }: { source: BindingSource }) {
  if (!source.changesData) return null;
  return (
    <p className="text-12 text-secondary" data-testid="binding-changes-data">
      {source.changesWhy === "declares no columns"
        ? "This query returns no columns; an entity can only read through a query that returns rows."
        : "This query changes data; an entity can only read through a query that returns rows."}
    </p>
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

// ------------------------------------------------------------------ one binding

function BindingCard({
  entityId,
  entityName,
  index,
  binding,
  databaseName,
  attributes,
  typeColumn,
  typeRole,
  derived,
  hierarchyDocs,
  walkCut,
  onEdit,
  onRemove,
}: {
  entityId: string;
  entityName: string;
  index: number;
  binding: Binding;
  databaseName: string;
  attributes: StorageAttribute[];
  /** The type column the binding names or inherits from a base's binding. */
  typeColumn: string | undefined;
  typeRole: TypeColumnRole;
  derived: string[];
  hierarchyDocs: ReadonlyMap<string, Rec>;
  walkCut: boolean;
  onEdit: (change: (b: Binding) => void) => void;
  onRemove: () => void;
}) {
  const { sources, pending, stale } = useBindingSources(binding.database, binding.source);
  const source = sources.find((s) => s.id === binding.source);
  const columns = source?.columns ?? [];
  // While the database's tables load, no row can be judged: a column that has not arrived is not a missing one.
  const loading = pending && !source;
  // Table per type and child tables: what the base's binding and the child tables store is not this field map's.
  const hostKey = plainTable(hostOf(binding));
  const baseKey = (() => {
    const b = boundBaseOf(entityId, binding.database, hierarchyDocs);
    return b ? plainTable(hostOf(b.binding)) : undefined;
  })();
  const tableDocs = useElements([hostKey, baseKey].filter((k): k is string => !!k));
  const shape = useMemo(
    () => bindingShape(entityId, binding, hierarchyDocs, (id) => tableDocs.byId.get(id)?.json as TableDoc | undefined),
    [entityId, binding, hierarchyDocs, tableDocs.byId],
  );
  const { base, host: hostDoc, baseHost: baseHostDoc, baseAttributes: baseAttrs } = shape;
  const { baseStored = new Set<string>(), childStored = new Set<string>() } = shape.extras;
  // A binding with a base foreign key leaves what the base stores to the base's binding; one that shares a per-type table keeps the rows
  // (it may map the key) but nothing it leaves unmapped is expected.
  const hidesBase = !!binding.baseForeignKey;
  const offered = attributes.filter((a) => !(hidesBase && baseStored.has(a.ref)) && !storedByChild(a.ref, childStored));
  const relations = useRelationColumns(entityId, binding.database, hierarchyDocs);
  const extras: ProblemExtras = {
    ...shape.extras,
    ...(relations.columns ? { relationColumns: relations.columns } : {}),
    ...(relations.incomplete ? { relationColumnsIncomplete: true } : {}),
  };
  const allRows = attributeRows(binding, offered, columns).map((r) =>
    r.status === "unmapped" && baseStored.has(r.attribute.ref) ? { ...r, status: "optional" as const } : r,
  );
  // A value object or list held whole in a JSON column shows its row only once a field maps it; "Store whole as JSON" adds one.
  const fieldRows = allRows.filter((r) => !(r.attribute.json && !r.field));
  const colRows = columnRows(binding, attributes, columns, typeColumn, extras);
  const validation = useValidation();
  const names = { entity: entityName, database: databaseName };
  const local =
    source || !pending
      ? [...bindingProblems(binding, attributes, source, names, typeColumn, extras), ...baseProblems(binding, base, hostDoc, baseHostDoc)]
      : [];
  const localRules = new Set(local.map((p) => p.rule));
  const at = `/bindings/${index}`;
  const mine = (validation.data?.diagnostics ?? []).filter(
    (d) => d.elementId === entityId && ((d.jsonPointer ?? "") === at || (d.jsonPointer ?? "").startsWith(`${at}/`)) && d.severity !== "info",
  );
  const asProblem = (d: (typeof mine)[number]): MapProblem => ({
    rule: d.rule,
    severity: d.severity === "error" ? "error" : "warning",
    message: d.message,
    pointer: d.jsonPointer ?? "",
  });
  const inChild = (d: (typeof mine)[number]) => (d.jsonPointer ?? "").startsWith(`${at}/childTables/`);
  const inBase = (d: (typeof mine)[number]) => (d.jsonPointer ?? "").startsWith(`${at}/baseForeignKey`);
  const server: MapProblem[] = mine.filter((d) => !localRules.has(d.rule) && !inChild(d) && !inBase(d)).map(asProblem);
  const baseLocal = local.filter((p) => p.pointer === "/baseForeignKey");
  const baseServer = mine.filter((d) => inBase(d) && !baseLocal.some((p) => p.rule === d.rule)).map(asProblem);
  const childServer = mine.filter(inChild).map(asProblem);
  const problems = [...local.filter((p) => p.pointer !== "/baseForeignKey"), ...server];
  const [mapping, setMapping] = useState(false);
  // Attributes from the source's columns (materialize-attributes): every column nothing in the binding names, or one column.
  const services = useServices();
  const [adding, setAdding] = useState(false);
  const unnamed = colRows.filter((r) => r.status === "unaccounted" || r.status === "identity" || r.status === "default");
  const canAdd = !!source && source.kind !== "query" && !adding;
  const addAttributes = async (columnKeys: string[] | null) => {
    setAdding(true);
    const n = columnKeys?.length ?? unnamed.length;
    const label =
      columnKeys?.length === 1 ? `New attribute for column ${findColumn(columns, columnKeys[0])?.name ?? columnKeys[0]}` : `New attributes for ${n} columns`;
    const failed = await commitMaterialize(
      services,
      label,
      { op: "materialize-attributes", database: binding.database, entities: [entityId], ...(columnKeys ? { columns: columnKeys } : {}) },
      { updates: [entityId], deletes: [] },
    );
    setAdding(false);
    if (failed) services.store.getState().notify(failed, "error");
  };
  // Columns for the attributes the binding leaves unmapped (materialize-columns): only a table file the binding reads and writes.
  const unmappedAttrs = fieldRows.filter((r) => (r.status === "unmapped" || r.status === "optional") && !r.attribute.json);
  const canAddColumns =
    !!source && source.kind === "table" && (typeof binding.write !== "object" || binding.write.table === binding.source) && binding.write !== "none" && !adding;
  const addColumns = async (refs: string[] | null) => {
    setAdding(true);
    const label =
      refs?.length === 1 ? `New column for ${attributes.find((a) => a.ref === refs[0])?.name ?? refs[0]}` : `New columns in ${source?.name ?? "the table"}`;
    const failed = await commitMaterialize(
      services,
      label,
      { op: "materialize-columns", database: binding.database, entities: [entityId], ...(refs ? { attributes: refs } : {}) },
      { updates: [binding.source, entityId], deletes: [] },
    );
    setAdding(false);
    if (failed) services.store.getState().notify(failed, "error");
  };
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
            sources={sources}
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

      {source?.changesData ? <ChangesDataNote source={source} /> : null}

      <Problems problems={problems} testid="binding-problems" />

      <ConstantsGrid dom={dom} binding={binding} columns={columns} onEdit={onEdit} />

      <TypeColumnSection
        dom={dom}
        entityName={entityName}
        binding={binding}
        role={typeRole}
        columns={columns}
        codes={typeRole.role === "none" ? [] : readCodes(entityId, binding, derived, hierarchyDocs)}
        walkCut={walkCut}
        onEdit={onEdit}
      />

      {base || binding.baseForeignKey ? (
        <BaseTableSection
          dom={dom}
          binding={binding}
          base={base ?? { entityId: "", name: "its base", binding: { id: "", database: binding.database, source: "" } }}
          baseAttributes={baseAttrs}
          host={hostDoc}
          baseHost={baseHostDoc}
          storedNames={baseAttrs.filter((a) => a.expected && !a.json && a.origin !== "relation").map((a) => a.name)}
          problems={[...baseLocal, ...baseServer]}
          onEdit={onEdit}
        />
      ) : null}

      <ChildTablesSection
        dom={dom}
        entityId={entityId}
        entityName={entityName}
        binding={binding}
        bindingIndex={index}
        docs={hierarchyDocs}
        host={hostDoc}
        tables={sources.filter((s) => s.kind === "table").map((s) => ({ id: s.id, name: s.name, schema: s.schema }))}
        server={childServer}
        databaseName={databaseName}
        onEdit={onEdit}
      />

      <section className="flex flex-col gap-1">
        <SectionTitle
          actions={
            <div className="flex items-center gap-1">
              <Button size="sm" variant="ghost" onClick={() => setMapping(true)} disabled={!source} data-testid="binding-map-by-name">
                Map by name…
              </Button>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => void addColumns(null)}
                disabled={!canAddColumns || !unmappedAttrs.length}
                title={
                  !source || source.kind !== "table"
                    ? "Columns are added to a table file: the source is not one."
                    : unmappedAttrs.length
                      ? `A column of ${source.name} per unmapped attribute (${unmappedAttrs.map((r) => r.attribute.name).join(", ")}), named and typed by the conventions, mapped to it.`
                      : "Every attribute is mapped."
                }
                data-testid="binding-add-columns"
              >
                <Plus /> Add columns for the unmapped attributes
              </Button>
            </div>
          }
        >
          Field map
        </SectionTitle>
        {hidesBase && baseStored.size ? (
          <p className="text-12 text-secondary" data-testid="field-map-base-stored">
            Stored by {base?.name ?? "the base"}&apos;s binding, not mapped here:{" "}
            {attributes
              .filter((a) => baseStored.has(a.ref) && !a.json)
              .map((a) => a.name)
              .join(", ") || "the inherited attributes"}
            .
          </p>
        ) : null}
        {childStored.size ? (
          <p className="text-12 text-secondary" data-testid="field-map-child-stored">
            Stored in child tables, not mapped here:{" "}
            {[...childStored].map((id) => attributes.find((a) => a.ref === id)?.name ?? nameOfAttribute(hierarchyDocs, entityId, id)).join(", ")}.
          </p>
        ) : null}
        <table className="w-full text-13" aria-label={`Fields of ${entityName} in ${databaseName}`} data-testid="binding-fields">
          <thead className="text-left text-11 text-secondary">
            <tr>
              <th className="font-medium">Attribute</th>
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
                  {r.attribute.json ? (
                    <span className="ml-1 font-sans text-11 text-secondary" data-testid="field-json">
                      as one JSON column
                    </span>
                  ) : null}
                  {originLabel(r.attribute) ? (
                    <span className="ml-1 font-sans text-11 text-secondary" title={originTitle(r.attribute)} data-testid="field-origin">
                      {originLabel(r.attribute)}
                    </span>
                  ) : null}
                </td>
                <td className="w-56 py-0.5">
                  <Select
                    aria-label={`Column of ${r.attribute.name}`}
                    className="h-6"
                    value={r.column?.key ?? (r.field ? r.field.column : "")}
                    onChange={(e) => {
                      if (e.target.value === NEW_COLUMN) {
                        void addColumns([r.attribute.ref]);
                        return;
                      }
                      const column = columns.find((c) => c.key === e.target.value) ?? null;
                      onEdit((b) => setField(b, r.attribute.ref, column, columns));
                    }}
                  >
                    <option value="">—</option>
                    {canAddColumns && !r.field ? <option value={NEW_COLUMN}>{`New column in ${source!.name}`}</option> : null}
                    {r.field && !r.column ? <option value={r.field.column}>{loading ? "Loading…" : `${r.field.column} (not found)`}</option> : null}
                    {columns.map((c) => (
                      <option key={c.key} value={c.key}>
                        {c.name}
                      </option>
                    ))}
                  </Select>
                </td>
                <td
                  className={cn(
                    "text-12",
                    loading ? "text-secondary" : r.status === "missing-column" ? "text-danger" : r.status === "unmapped" ? "text-warning" : "text-secondary",
                  )}
                  data-testid={loading ? "field-status-loading" : undefined}
                >
                  {loading
                    ? "Loading…"
                    : {
                        mapped: "mapped",
                        "missing-column": "no such column",
                        unmapped: "not mapped",
                        optional: "not mapped (optional)",
                        whole: "held whole above",
                      }[r.status]}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <StoreWholeAsJson
          rows={offered}
          fields={binding.fields ?? []}
          columns={columns}
          onPick={(ref, column) => onEdit((b) => setField(b, ref, column, columns))}
        />
        {relations.incomplete ? (
          <p className="text-12 text-secondary" data-testid="relation-columns-incomplete">
            Some relationships were not read (the list is cut at {WALK_LIMITS.relations}): a column that looks unaccounted may be a relationship&apos;s
            attribute.
          </p>
        ) : null}
      </section>

      <section className="flex flex-col gap-1">
        <SectionTitle
          actions={
            <Button
              size="sm"
              variant="ghost"
              onClick={() => void addAttributes(null)}
              disabled={!canAdd || !unnamed.length}
              title={
                source?.kind === "query"
                  ? "A query's fields become attributes by hand."
                  : unnamed.length
                    ? `An attribute per column nothing maps (${unnamed.map((r) => r.column.name).join(", ")}), named and typed from the column and mapped to it.`
                    : "Every column is accounted for."
              }
              data-testid="binding-add-attributes"
            >
              <Plus />{" "}
              {unnamed.length
                ? `Add attributes for the ${unnamed.length} unmapped ${unnamed.length === 1 ? "column" : "columns"}`
                : "Add attributes for the unmapped columns"}
            </Button>
          }
        >
          Columns of {source?.name ?? "the source"}
        </SectionTitle>
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
              const free =
                r.status !== "field" &&
                r.status !== "constant" &&
                r.status !== "discriminator" &&
                r.status !== "soft-delete" &&
                r.status !== "relation" &&
                r.status !== "base-key";
              return (
                <tr key={r.column.key} data-testid={`column-row-${r.column.name}`} data-status={r.status}>
                  <td className="font-mono">{r.column.name}</td>
                  <td className="text-12 text-secondary">
                    {r.column.type ?? ""}
                    {r.column.nullable ? "" : " not null"}
                  </td>
                  <td className={cn("text-12", r.status === "unaccounted" ? "text-warning" : "text-secondary")}>
                    {loading ? "Loading…" : statusText(r.status, r.attribute?.name ?? r.fieldRef, r.constant?.value)}
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
                          onChange={(e) => {
                            if (e.target.value === "attribute") void addAttributes([r.column.key]);
                            else onEdit((b) => setColumnStatus(b, r.column, (e.target.value || null) as ListedStatus | null, columns));
                          }}
                        >
                          <option value="">(its own: {r.status === "unaccounted" ? "nothing" : COLUMN_STATUS_LABELS[r.status]})</option>
                          <option value="ignored">Ignored</option>
                          <option value="database">Filled by the database</option>
                          <option value="computed">Computed</option>
                          {canAdd ? <option value="attribute">New attribute, mapped to it</option> : null}
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
          typeColumn={typeColumn}
          onClose={() => setMapping(false)}
          onApply={(refs) => {
            const matches = matchByName(binding, attributes, columns, typeColumn).filter((m) => refs.includes(m.attribute.ref));
            if (matches.length) onEdit((b) => applyMatches(b, matches, columns));
            setMapping(false);
          }}
        />
      ) : null}
    </section>
  );
}

/** Where an attribute the entity does not declare itself comes from, shown after its name; empty for its own attributes. */
function originLabel(a: StorageAttribute): string {
  switch (a.origin) {
    case "inherited":
      return `from ${a.from ?? "a base entity"}`;
    case "virtual":
      return a.from ?? "stereotype";
    case "member":
      return `in ${a.from ?? "a value object"}`;
    case "relation":
      return `via ${a.from ?? "a relationship"}`;
    default:
      return "";
  }
}

function originTitle(a: StorageAttribute): string {
  switch (a.origin) {
    case "inherited":
      return `Inherited from ${a.from ?? "a base entity"}`;
    case "virtual":
      return `Added by the stereotype ${a.from ?? ""}`;
    case "member":
      return `A member of the value object attribute ${a.from ?? ""}`;
    case "relation":
      return `The key of the to-one navigation of ${a.from ?? "a relationship"}`;
    default:
      return "";
  }
}

/** An attribute's name from the documents at hand (a child table's attribute is no field of the map). */
function nameOfAttribute(docs: ReadonlyMap<string, Rec>, entityId: string, id: string): string {
  for (let at: string | undefined = entityId, guard = 0; at && guard < 64; at = docs.get(at)?.base as string | undefined, guard++) {
    const hit = ((docs.get(at)?.attributes as { id: string; name: string }[] | undefined) ?? []).find((a) => a.id === id);
    if (hit) return hit.name;
  }
  return id;
}

function statusText(status: ColumnStatus, attribute: string | undefined, value: unknown): string {
  if (status === "field") return `mapped by ${attribute ?? "?"}`;
  if (status === "base-key") return `key ${attribute ?? ""} from the base`.replace("  ", " ");
  if (status === "constant") return `constant ${value === undefined || value === null ? "NULL" : JSON.stringify(value)}`;
  return COLUMN_STATUS_LABELS[status];
}

// ------------------------------------------------------------------ constants

/**
 * The type column of a hierarchy kept in one table. A base's binding names the column and its own code; a derived entity's binding
 * reads the column from its bases (read-only) and sets its own code, which defaults to the entity's name. Left out of an entity
 * outside a hierarchy.
 */
function TypeColumnSection({
  dom,
  entityName,
  binding,
  role,
  columns,
  codes,
  walkCut,
  onEdit,
}: {
  dom: string;
  entityName: string;
  binding: Binding;
  role: TypeColumnRole;
  columns: BindingSource["columns"];
  codes: (string | number)[];
  /** The walk down the derived entities stopped at its limit, so the list of codes may be short. */
  walkCut: boolean;
  onEdit: (change: (b: Binding) => void) => void;
}) {
  if (role.role === "none") return null;
  // The code's JSON type follows the column's: a number for an integer column, text otherwise (the engine refuses the other, MQ4125).
  const columnName = binding.discriminatorColumn ?? (role.role === "derived" ? role.column : "");
  const typeOf = (name: string): boolean | null => {
    const found = findColumn(columns, name);
    return found ? isIntegerColumn(found.type) : null;
  };
  const integer = typeOf(columnName);
  const code = (
    <Field label="Type code" htmlFor={`${dom}-type-code`} className="w-40">
      <Input
        id={`${dom}-type-code`}
        className="h-6 font-mono"
        placeholder={entityName}
        data-testid="binding-type-code"
        key={String(binding.discriminatorValue ?? "")}
        defaultValue={binding.discriminatorValue === undefined ? "" : String(binding.discriminatorValue)}
        onBlur={(e) => {
          // Compared by value and type, so retyping a code that has the wrong JSON type fixes it.
          if (!sameCode(codeValue(e.target.value, integer, binding.discriminatorValue), binding.discriminatorValue))
            onEdit((b) => setTypeCode(b, e.target.value, integer));
        }}
        onKeyDown={(e) => {
          if (e.key === "Enter") (e.target as HTMLInputElement).blur();
        }}
      />
    </Field>
  );
  const keeps = (
    <span className="pb-1 text-12 text-secondary" data-testid="binding-type-codes">
      Reads keep: {codes.map(String).join(", ")}
      {walkCut ? " (and more: the list of derived entities was cut)" : ""}
    </span>
  );
  return (
    <section className="flex flex-col gap-1" data-testid="binding-type-column" data-role={role.role}>
      <SectionTitle>Type column</SectionTitle>
      {role.role === "base" ? (
        <div className="flex flex-wrap items-end gap-2">
          <Field label="Column" htmlFor={`${dom}-type-column`} className="w-56">
            <Select
              id={`${dom}-type-column`}
              data-testid="binding-type-column-select"
              value={findColumn(columns, binding.discriminatorColumn ?? "")?.key ?? binding.discriminatorColumn ?? ""}
              onChange={(e) => {
                const column = columns.find((c) => c.key === e.target.value) ?? null;
                onEdit((b) => retypeCode(setTypeColumn(b, column), column ? isIntegerColumn(column.type) : null));
              }}
            >
              <option value="">None</option>
              {binding.discriminatorColumn && !findColumn(columns, binding.discriminatorColumn) ? (
                <option value={binding.discriminatorColumn}>{`${binding.discriminatorColumn} (not found)`}</option>
              ) : null}
              {columns.map((c) => (
                <option key={c.key} value={c.key}>
                  {c.name}
                </option>
              ))}
            </Select>
          </Field>
          {code}
          {binding.discriminatorColumn ? keeps : null}
        </div>
      ) : (
        <div className="flex flex-wrap items-end gap-2">
          <p className="pb-1 text-12" data-testid="binding-type-column-inherited">
            Type column <span className="font-mono">{findColumn(columns, role.column)?.name ?? role.column}</span>, from {role.base}
          </p>
          {role.redundant || !role.canName ? null : (
            <Field label="Own column" htmlFor={`${dom}-type-column`} className="w-48">
              <Select
                id={`${dom}-type-column`}
                data-testid="binding-type-column-select"
                value={binding.discriminatorColumn ? (findColumn(columns, binding.discriminatorColumn)?.key ?? binding.discriminatorColumn) : ""}
                onChange={(e) => {
                  const column = columns.find((c) => c.key === e.target.value) ?? null;
                  onEdit((b) => retypeCode(setTypeColumn(b, column), column ? isIntegerColumn(column.type) : typeOf(role.column)));
                }}
              >
                <option value="">{`Use ${role.base}'s`}</option>
                {columns.map((c) => (
                  <option key={c.key} value={c.key}>
                    {c.name}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          {code}
          {keeps}
          {role.redundant ? (
            <span className="flex items-center gap-1 pb-0.5 text-12 text-danger" data-testid="binding-type-column-redundant">
              This binding names a type column too, which is refused: the base's applies.
              <Button size="sm" variant="ghost" onClick={() => onEdit((b) => setTypeColumn(b, null))}>
                Remove it
              </Button>
            </span>
          ) : null}
        </div>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ Map by name

function MapByNameDialog({
  binding,
  attributes,
  source,
  typeColumn,
  onClose,
  onApply,
}: {
  binding: Binding;
  attributes: StorageAttribute[];
  source: BindingSource;
  typeColumn: string | undefined;
  onClose: () => void;
  onApply: (refs: string[]) => void;
}) {
  const matches = matchByName(binding, attributes, source.columns, typeColumn);
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
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const sql = useQuery({
    queryKey: ["preview", "binding-sql", entityId, bindingId, dialect],
    queryFn: () => endpoints.getBindingSql(entityId, bindingId, dialect || null),
    enabled: open,
  });
  const preview = sql.data?.preview;
  const statement = (testid: string, label: string, st: { sql: string } | null | undefined, none: string) => (
    <div key={testid} data-testid={testid}>
      <p className="font-medium text-secondary">{label}</p>
      {st ? <pre className="overflow-auto rounded-control bg-app p-1 font-mono text-12">{st.sql}</pre> : <p className="text-secondary">{none}</p>}
    </div>
  );
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
        {sql.data && !preview ? (
          <ul className="text-danger">
            {sql.data.diagnostics.slice(0, 5).map((d, i) => (
              <li key={i}>
                <span className="font-mono">{d.rule}</span> {d.message}
              </li>
            ))}
          </ul>
        ) : null}
        {preview ? (
          <>
            {STATEMENTS.map(([key, label]) =>
              statement(
                `binding-sql-${key}`,
                label,
                preview[key],
                `None: the binding does not ${key === "insert" || key === "update" ? "write" : key === "delete" ? "delete" : "read by key"}.`,
              ),
            )}
            {preview.exists ? statement("binding-sql-exists", "Exists (is there a row with the key in this table)", preview.exists, "") : null}
            {(preview.bases ?? []).map((b) => (
              <div key={b.bindingId} className="flex flex-col gap-1 border-l-2 border-default pl-2" data-testid="binding-sql-base" data-level={b.level}>
                <p className="font-semibold">
                  Table above, level {b.level}: the binding of {lookup.nameOf(b.entityId) ?? b.entityId}
                </p>
                {statement(`binding-sql-base-${b.level}-insert`, "Insert", b.insert, "None: the base's binding does not write.")}
                {statement(`binding-sql-base-${b.level}-update`, "Update", b.update, "None.")}
                {statement(`binding-sql-base-${b.level}-delete`, "Delete", b.delete, "None.")}
              </div>
            ))}
            {(preview.childTables ?? []).map((c) => (
              <div key={c.id} className="flex flex-col gap-1 border-l-2 border-default pl-2" data-testid="binding-sql-child" data-attribute={c.attributeName}>
                <p className="font-semibold">Child table of {c.attributeName}</p>
                {statement(`binding-sql-child-${c.attributeName}-select`, "Select (the rows of one owner, in order)", c.select, "None.")}
                {statement(`binding-sql-child-${c.attributeName}-insert`, "Insert (one row)", c.insert, "None: the binding does not write.")}
                {statement(`binding-sql-child-${c.attributeName}-delete`, "Delete (the rows of one owner)", c.delete, "None: the binding does not write.")}
              </div>
            ))}
          </>
        ) : null}
      </div>
    </details>
  );
}
