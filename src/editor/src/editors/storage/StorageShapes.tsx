// The Storage tab's two sections for shapes a binding takes beyond one table (retire-projection-design.md sections 4 and 5): the base
// table of a derived entity (table per type: the foreign key that joins its table to its base's) and the child tables (an attribute
// stored in a table of its own: a list, or a single value object). Pure logic is in baseTable.ts and childTables.ts; the sections
// edit the binding through the card's `onEdit`, so each gesture is one save and one undo step.
import { useState } from "react";
import { CircleAlert, Plus, Trash2, TriangleAlert } from "lucide-react";
import { useElements } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { newId } from "@/lib/ids";
import { ConstantsGrid } from "./ConstantsGrid";
import { baseKeyChoices, joinedKeyColumns, setBaseForeignKey, type BoundBase } from "./baseTable";
import {
  childAttribute,
  childCandidates,
  childColumnRows,
  childProblems,
  childTableComplete,
  itemAttributes,
  newChildTable,
  ownerColumnsOf,
  ownerKeyChoices,
  pickChildTable,
  removeChildTable,
  setOrderColumn,
  setOwnerKey,
  upsertChildTable,
} from "./childTables";
import {
  attributeRows,
  COLUMN_STATUS_LABELS,
  findColumn,
  setColumnStatus,
  setField,
  type Binding,
  type BindingField,
  type ChildTable,
  type ListedStatus,
  type MapProblem,
  type SourceColumn,
  type StorageAttribute,
  wholeAbove,
} from "./fieldMap";
import { foreignKeyLabel, plainTable, tableDocColumns, type TableDoc } from "./tableDoc";
import { hostOf, isIntegerColumn } from "./typeColumn";
import { useServices } from "@/app/context";
import { keys, loadElement } from "@/api/queries";

type Json = Record<string, unknown>;

/** A list of problems as the binding card shows them: the rule id first, errors in the danger colour. */
export function Problems({ problems, testid }: { problems: readonly MapProblem[]; testid: string }) {
  if (!problems.length) return null;
  return (
    <ul className="flex flex-col gap-0.5 text-12" data-testid={testid}>
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
  );
}

// ------------------------------------------------------------------ the base table

/**
 * The base table of a derived entity: the foreign key of the binding's table that references the table of the base's binding, with the
 * key columns it joins. The inherited attributes and to-one ends are stored by the base's binding, so the field map does not offer them.
 */
export function BaseTableSection({
  dom,
  binding,
  base,
  baseAttributes,
  host,
  baseHost,
  storedNames,
  problems,
  onEdit,
}: {
  dom: string;
  binding: Binding;
  base: BoundBase;
  baseAttributes: readonly StorageAttribute[];
  /** The document of the table the binding writes (else its source), when it is a table file. */
  host: TableDoc | undefined;
  baseHost: TableDoc | undefined;
  /** The names of what the base's binding stores for this entity, shown when the join is set. */
  storedNames: readonly string[];
  problems: readonly MapProblem[];
  onEdit: (change: (b: Binding) => void) => void;
}) {
  const sameTable = hostOf(binding) === hostOf(base.binding);
  const fits = baseKeyChoices(host, plainTable(hostOf(base.binding)));
  const value = binding.baseForeignKey ?? "";
  const current = host?.foreignKeys?.find((k) => k.id === value);
  const joined = current ? joinedKeyColumns(current, host, baseHost, base.binding, baseAttributes) : [];
  return (
    <section className="flex flex-col gap-1" data-testid="binding-base-table" data-joined={value ? "yes" : "no"}>
      <SectionTitle>Base table</SectionTitle>
      <div className="flex flex-wrap items-end gap-2">
        {sameTable ? (
          <p className="pb-1 text-12 text-secondary" data-testid="base-table-same">
            This binding keeps its rows in the table of {base.name}&apos;s binding: no join. Bind it to a table of its own to join them.
          </p>
        ) : (
          <Field label={`Foreign key to the table of ${base.name}`} htmlFor={`${dom}-base-fk`} className="w-96">
            <Select
              id={`${dom}-base-fk`}
              data-testid="binding-base-fk"
              value={value}
              disabled={!host}
              onChange={(e) => {
                const id = e.target.value || null;
                const stored = new Set(baseAttributes.map((a) => a.ref));
                onEdit((b) => setBaseForeignKey(b, id, stored));
              }}
            >
              <option value="">None: this entity&apos;s own rows only</option>
              {value && !current ? <option value={value}>{`${value} (not found)`}</option> : null}
              {current && !fits.some((k) => k.id === current.id) ? (
                <option value={current.id}>{`${foreignKeyLabel(current, host, baseHost)} (does not fit)`}</option>
              ) : null}
              {fits.map((k) => (
                <option key={k.id} value={k.id}>
                  {foreignKeyLabel(k, host, baseHost)}
                </option>
              ))}
            </Select>
          </Field>
        )}
        {!sameTable && host && !fits.length && !value ? (
          <span className="pb-1 text-12 text-secondary" data-testid="base-table-no-keys">
            No foreign key of {host.name ?? "this table"} to the table of {base.name} has the table&apos;s whole primary key as its columns.
          </span>
        ) : null}
      </div>
      {value && joined.length ? (
        <table className="w-full text-13" aria-label="Columns the base foreign key joins" data-testid="binding-base-key">
          <thead className="text-left text-11 text-secondary">
            <tr>
              <th className="font-medium">Column</th>
              <th className="font-medium">References</th>
              <th className="font-medium">Key</th>
            </tr>
          </thead>
          <tbody>
            {joined.map((j) => (
              <tr key={j.column} data-testid="base-key-row">
                <td className="font-mono">{j.name}</td>
                <td className="font-mono text-12 text-secondary">{j.referenced}</td>
                <td className="text-12 text-secondary">{j.attribute ? `${j.attribute}, from ${base.name}` : `from ${base.name}`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      {value ? (
        <p className="text-12 text-secondary" data-testid="base-table-stored">
          The key comes from this foreign key. {base.name}&apos;s binding stores {storedNames.length ? storedNames.join(", ") : "the inherited attributes"}.
        </p>
      ) : null}
      <Problems problems={problems} testid="base-table-problems" />
    </section>
  );
}

// ------------------------------------------------------------------ child tables

const LISTED: ListedStatus[] = ["ignored", "database", "computed"];

/**
 * One row per attribute stored in a table of its own (lists and value objects the entity has or inherits, unless a base's binding stores
 * it). Add, edit and remove an entry; each edit is one save.
 */
export function ChildTablesSection({
  dom,
  entityId,
  entityName,
  binding,
  bindingIndex,
  docs,
  host,
  tables,
  server,
  databaseName,
  onEdit,
}: {
  dom: string;
  entityId: string;
  entityName: string;
  binding: Binding;
  bindingIndex: number;
  docs: ReadonlyMap<string, Json>;
  /** The document of the table that owns the child rows: the binding's write table (else its source). */
  host: TableDoc | undefined;
  tables: { id: string; name: string; schema: string | null }[];
  /** The engine's findings on the entity, from the validation. */
  server: readonly { rule: string; severity: "error" | "warning"; message: string; pointer: string }[];
  databaseName: string;
  onEdit: (change: (b: Binding) => void) => void;
}) {
  const saved = binding.childTables ?? [];
  // An entry is saved once it names its table and owner key (the file requires both); until then it lives here, apart from the document.
  const [drafts, setDrafts] = useState<Record<string, ChildTable>>({});
  const entries = [...saved.map((e) => drafts[e.id] ?? e), ...Object.values(drafts).filter((d) => !saved.some((e) => e.id === d.id))];
  const candidates = childCandidates(
    entityId,
    binding,
    docs,
    Object.values(drafts).map((d) => d.attribute),
  );
  const editEntry = (base: ChildTable, change: (e: ChildTable) => void) => {
    const next = structuredClone(drafts[base.id] ?? base);
    change(next);
    if (childTableComplete(next)) {
      setDrafts(({ [base.id]: _gone, ...rest }) => rest);
      onEdit((b) => void upsertChildTable(b, next));
    } else setDrafts((d) => ({ ...d, [base.id]: next }));
  };
  const removeEntry = (id: string) => {
    setDrafts(({ [id]: _gone, ...rest }) => rest);
    if (saved.some((e) => e.id === id)) onEdit((b) => void removeChildTable(b, id));
  };
  const [picked, setPicked] = useState("");
  const choice = candidates.find((c) => c.id === picked) ?? candidates[0];
  if (!entries.length && !candidates.length) return null;
  return (
    <section className="flex flex-col gap-1" data-testid="binding-child-tables">
      <SectionTitle
        actions={
          <div className="flex items-center gap-1">
            <Select
              aria-label="Attribute for a new child table"
              className="h-6 w-48"
              value={choice?.id ?? ""}
              onChange={(e) => setPicked(e.target.value)}
              disabled={!candidates.length}
              data-testid="child-table-attribute"
            >
              {candidates.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                  {c.isList ? " (list)" : " (value object)"}
                  {c.from ? ` from ${c.from}` : ""}
                </option>
              ))}
            </Select>
            <Button
              size="sm"
              variant="ghost"
              disabled={!choice}
              data-testid="child-table-add"
              title={choice ? `Store ${choice.name} in a table of its own.` : "Every list and value object is stored already."}
              onClick={() => {
                if (!choice) return;
                const entry = newChildTable(newId(), choice.id);
                setDrafts((d) => ({ ...d, [entry.id]: entry }));
                setPicked("");
              }}
            >
              <Plus /> Add child table
            </Button>
          </div>
        }
      >
        Child tables
      </SectionTitle>
      {entries.length ? null : <p className="text-12 text-secondary">None: lists and value objects are not stored by this binding.</p>}
      {entries.map((entry, i) => {
        const at = saved.findIndex((e) => e.id === entry.id);
        return (
          <ChildEntry
            key={entry.id}
            dom={`${dom}-child-${i}`}
            entityId={entityId}
            entityName={entityName}
            binding={binding}
            entry={entry}
            incomplete={!childTableComplete(entry) || !!drafts[entry.id]}
            pointer={at >= 0 && !drafts[entry.id] ? `/bindings/${bindingIndex}/childTables/${at}` : null}
            docs={docs}
            host={host}
            tables={tables}
            server={server}
            databaseName={databaseName}
            onEdit={(change) => editEntry(entry, change)}
            onRemove={() => removeEntry(entry.id)}
          />
        );
      })}
    </section>
  );
}

function ChildEntry({
  dom,
  entityId,
  entityName,
  binding,
  entry,
  incomplete,
  pointer,
  docs,
  host,
  tables,
  server,
  databaseName,
  onEdit,
  onRemove,
}: {
  dom: string;
  entityId: string;
  entityName: string;
  binding: Binding;
  entry: ChildTable;
  /** Not saved yet: it lacks its table or owner key. */
  incomplete: boolean;
  /** The entry's JSON pointer in the entity file, when it is saved. */
  pointer: string | null;
  docs: ReadonlyMap<string, Json>;
  host: TableDoc | undefined;
  tables: { id: string; name: string; schema: string | null }[];
  server: readonly { rule: string; severity: "error" | "warning"; message: string; pointer: string }[];
  databaseName: string;
  onEdit: (change: (e: ChildTable) => void) => void;
  onRemove: () => void;
}) {
  const services = useServices();
  const tableId = plainTable(entry.table);
  const read = useElements(tableId ? [tableId] : []);
  const child = tableId ? (read.byId.get(tableId)?.json as TableDoc | undefined) : undefined;
  const loading = !!tableId && read.pending && !child;
  const columns = tableDocColumns(child);
  const attribute = childAttribute(entityId, entry.attribute, docs);
  const isList = attribute?.collection === true;
  const items = itemAttributes(attribute, docs);
  const ownerTable = plainTable(hostOf(binding));
  const fits = ownerKeyChoices(child, ownerTable, host);
  const owned = ownerColumnsOf(entry, child);
  const allRows = attributeRows(entry, items, columns, entry.fields ?? []);
  const rows = allRows.filter((r) => !(r.attribute.json && !r.field));
  const colRows = childColumnRows(entry, items, columns, child);
  const tableName = child?.name ?? tables.find((t) => t.id === entry.table)?.name ?? entry.table ?? "the table";
  const baseOwnsKey = !!binding.baseForeignKey;
  const local =
    loading || !entry.table
      ? []
      : childProblems(
          entry,
          attribute,
          items,
          columns,
          child,
          host,
          binding,
          !!child || !entry.table || !loading,
          { entity: entityName, table: tableName },
          baseOwnsKey,
        );
  // The engine's findings at this entry (exactly, or below it: entry 1 is not entry 10), unless they repeat a local one.
  const within = (p: { pointer: string }) => pointer !== null && (p.pointer === pointer || p.pointer.startsWith(`${pointer}/`));
  const shown: MapProblem[] = [
    ...local,
    ...server
      .filter((p) => within(p))
      .map((p) => ({ ...p, pointer: p.pointer.slice(pointer?.length ?? 0) }))
      .filter((p) => !local.some((l) => l.rule === p.rule && l.pointer === p.pointer && l.message === p.message)),
  ];
  // The columns a field may take: not the owner key, the position or a column another field reads.
  const usedElsewhere = (ref: string) => new Set((entry.fields ?? []).filter((f) => f.attribute !== ref).map((f) => findColumn(columns, f.column)?.key));
  const free = (ref: string): SourceColumn[] =>
    columns.filter((c) => !owned.has(c.key) && findColumn(columns, entry.orderColumn ?? "") !== c && !usedElsewhere(ref).has(c.key));
  const pickTable = async (id: string) => {
    if (!id) return;
    const doc = (await services.queryClient.fetchQuery({ queryKey: keys.element(id), queryFn: () => loadElement(id), staleTime: 30_000 })).json as TableDoc;
    const cols = tableDocColumns(doc);
    onEdit((e) => void pickChildTable(e, doc, cols, ownerKeyChoices(doc, ownerTable, host), items, isList, attribute?.name ?? ""));
  };
  const integers = columns.filter(
    (c) => isIntegerColumn(c.type) && !owned.has(c.key) && !(entry.fields ?? []).some((f) => findColumn(columns, f.column) === c),
  );
  return (
    <div
      className="flex flex-col gap-1.5 rounded-control border border-default p-1.5"
      data-testid="child-table-entry"
      data-attribute={attribute?.name ?? entry.attribute}
    >
      <div className="flex flex-wrap items-end gap-2">
        <h4 className="pb-1 text-13 font-semibold" data-testid="child-table-title">
          {attribute?.name ?? entry.attribute}
          <span className="ml-1 text-11 font-normal text-secondary">{isList ? "list, one row per item" : "single value object, at most one row"}</span>
        </h4>
        <Field label="Table" htmlFor={`${dom}-table`} className="w-56">
          <Select id={`${dom}-table`} data-testid="child-table-table" value={entry.table} onChange={(e) => void pickTable(e.target.value)}>
            <option value="">Pick a table</option>
            {entry.table && !tables.some((t) => t.id === entry.table) ? <option value={entry.table}>{`${entry.table} (not found)`}</option> : null}
            {tables.map((t) => (
              <option key={t.id} value={t.id}>
                {t.schema ? `${t.schema}.${t.name}` : t.name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label={`Owner key: foreign key to ${host?.name ?? "the owning table"}`} htmlFor={`${dom}-owner`} className="w-80">
          <Select
            id={`${dom}-owner`}
            data-testid="child-table-owner"
            value={entry.ownerForeignKey}
            disabled={!child}
            onChange={(e) => {
              const fk = child?.foreignKeys?.find((k) => k.id === e.target.value) ?? null;
              onEdit((x) => void setOwnerKey(x, fk, child, columns));
            }}
          >
            <option value="">Pick a foreign key</option>
            {entry.ownerForeignKey && !fits.some((k) => k.id === entry.ownerForeignKey) ? (
              <option
                value={entry.ownerForeignKey}
              >{`${child?.foreignKeys?.find((k) => k.id === entry.ownerForeignKey)?.name ?? entry.ownerForeignKey} (does not fit)`}</option>
            ) : null}
            {fits.map((k) => (
              <option key={k.id} value={k.id}>
                {foreignKeyLabel(k, child, host)}
              </option>
            ))}
          </Select>
        </Field>
        {isList ? (
          <Field label="Order column" htmlFor={`${dom}-order`} className="w-44">
            <Select
              id={`${dom}-order`}
              data-testid="child-table-order"
              value={entry.orderColumn ? (findColumn(columns, entry.orderColumn)?.key ?? entry.orderColumn) : ""}
              disabled={!child}
              onChange={(e) => {
                const column = columns.find((c) => c.key === e.target.value) ?? null;
                onEdit((x) => void setOrderColumn(x, column, columns));
              }}
            >
              <option value="">None: items have no position</option>
              {entry.orderColumn && !findColumn(columns, entry.orderColumn) ? (
                <option value={entry.orderColumn}>{`${entry.orderColumn} (not found)`}</option>
              ) : null}
              {entry.orderColumn && findColumn(columns, entry.orderColumn) && !integers.includes(findColumn(columns, entry.orderColumn)!) ? (
                <option value={findColumn(columns, entry.orderColumn)!.key}>{`${findColumn(columns, entry.orderColumn)!.name} (not an integer)`}</option>
              ) : null}
              {integers.map((c) => (
                <option key={c.key} value={c.key}>
                  {c.name}
                </option>
              ))}
            </Select>
          </Field>
        ) : null}
        <span className="flex-1" />
        <Button
          size="icon-sm"
          variant="ghost"
          label={`Remove the child table for ${attribute?.name ?? "this attribute"} (the table stays)`}
          onClick={onRemove}
          data-testid="child-table-remove"
        >
          <Trash2 />
        </Button>
      </div>
      {incomplete ? (
        <p className="text-12 text-warning" data-testid="child-table-incomplete">
          Not saved yet: {entry.table ? "pick the owner key" : "pick the table"} (a child table needs both to be saved).
        </p>
      ) : null}
      <Problems problems={shown} testid="child-table-problems" />
      {child ? (
        <>
          <table className="w-full text-13" aria-label={`Fields of ${attribute?.name ?? "the attribute"} in ${tableName}`} data-testid="child-table-fields">
            <thead className="text-left text-11 text-secondary">
              <tr>
                <th className="font-medium">{isList ? "Item" : "Member"}</th>
                <th className="font-medium">Column</th>
                <th className="font-medium">Status</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.attribute.ref} data-testid={`child-field-row-${r.attribute.name}`} data-status={r.status}>
                  <td className="font-mono">
                    {r.attribute.name}
                    {r.attribute.json ? <span className="ml-1 font-sans text-11 text-secondary">as one JSON column</span> : null}
                  </td>
                  <td className="w-56 py-0.5">
                    <Select
                      aria-label={`Column of ${r.attribute.name} in ${tableName}`}
                      className="h-6"
                      value={r.column?.key ?? r.field?.column ?? ""}
                      onChange={(e) => {
                        const column = columns.find((c) => c.key === e.target.value) ?? null;
                        onEdit((x) => void setField(x, r.attribute.ref, column, columns));
                      }}
                    >
                      <option value="">—</option>
                      {r.field && !r.column ? <option value={r.field.column}>{`${r.field.column} (not found)`}</option> : null}
                      {free(r.attribute.ref).map((c) => (
                        <option key={c.key} value={c.key}>
                          {c.name}
                        </option>
                      ))}
                    </Select>
                  </td>
                  <td className={cn("text-12", r.status === "missing-column" ? "text-danger" : r.status === "unmapped" ? "text-warning" : "text-secondary")}>
                    {
                      {
                        mapped: "mapped",
                        "missing-column": "no such column",
                        unmapped: "not mapped",
                        optional: "not mapped (optional)",
                        whole: "held whole above",
                      }[r.status]
                    }
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <StoreWholeAsJson
            rows={items}
            fields={entry.fields ?? []}
            columns={free("")}
            onPick={(ref, column) => onEdit((x) => void setField(x, ref, column, columns))}
          />
          <ConstantsGrid
            dom={`${dom}-constants`}
            binding={entry}
            columns={columns.filter((c) => !owned.has(c.key))}
            title="Constants"
            emptyText="None. Entries that share this table each need a constant on one column, with different values."
            skipPrimaryKey
            onEdit={(change) => onEdit((x) => change(x))}
          />
          <table className="w-full text-13" aria-label={`Columns of ${tableName}`} data-testid="child-table-columns">
            <thead className="text-left text-11 text-secondary">
              <tr>
                <th className="font-medium">Column</th>
                <th className="font-medium">Status</th>
                <th className="font-medium">Account for</th>
              </tr>
            </thead>
            <tbody>
              {colRows.map((r) => {
                const listed = (entry.columns ?? []).find((c) => findColumn(columns, c.column) === r.column);
                const open = r.status !== "field" && r.status !== "constant" && r.status !== "owner" && r.status !== "order";
                return (
                  <tr key={r.column.key} data-testid={`child-column-row-${r.column.name}`} data-status={r.status}>
                    <td className="font-mono">{r.column.name}</td>
                    <td className={cn("text-12", r.status === "unaccounted" ? "text-warning" : "text-secondary")}>
                      {r.status === "field" ? `mapped by ${r.attribute?.name ?? r.fieldRef ?? "?"}` : COLUMN_STATUS_LABELS[r.status]}
                    </td>
                    <td className="py-0.5">
                      {open ? (
                        <Select
                          aria-label={`What accounts for ${r.column.name} in ${tableName}`}
                          className="h-6 w-44"
                          value={listed?.status ?? ""}
                          onChange={(e) => onEdit((x) => void setColumnStatus(x, r.column, (e.target.value || null) as ListedStatus | null, columns))}
                        >
                          <option value="">(its own: {r.status === "unaccounted" ? "nothing" : COLUMN_STATUS_LABELS[r.status]})</option>
                          {LISTED.map((s) => (
                            <option key={s} value={s}>
                              {{ ignored: "Ignored", database: "Filled by the database", computed: "Computed" }[s]}
                            </option>
                          ))}
                        </Select>
                      ) : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </>
      ) : (
        <p className="text-12 text-secondary">
          {loading ? `Loading ${databaseName}…` : entry.table ? "The table is not a table file of the database." : "Pick the table that holds the rows."}
        </p>
      )}
    </div>
  );
}

/**
 * "Store whole as JSON…": a value object or list (nested ones included) held whole in one JSON column. Its row is left out of the field map
 * until a field maps it, so the map stays short; the action picks the attribute, then a column that can hold JSON.
 */
export function StoreWholeAsJson({
  rows,
  fields,
  columns,
  onPick,
}: {
  rows: readonly StorageAttribute[];
  fields: readonly BindingField[];
  columns: readonly SourceColumn[];
  onPick: (ref: string, column: SourceColumn) => void;
}) {
  const [ref, setRef] = useState("");
  const candidates = rows.filter(
    (r) => r.json && !fields.some((f) => f.attribute === r.ref || f.attribute.startsWith(`${r.ref}.`)) && !wholeAbove(r.ref, fields),
  );
  if (!candidates.length) return null;
  const used = new Set(fields.map((f) => findColumn(columns, f.column)?.key));
  const jsonColumns = columns.filter((c) => /json/i.test(c.type ?? "") && !used.has(c.key));
  const chosen = candidates.find((c) => c.ref === ref);
  return (
    <div className="flex flex-wrap items-center gap-1 text-12" data-testid="store-whole-json">
      <Select
        aria-label="Store whole as JSON"
        className="h-6 w-56"
        value={chosen?.ref ?? ""}
        onChange={(e) => setRef(e.target.value)}
        data-testid="store-whole-json-attribute"
      >
        <option value="">Store whole as JSON…</option>
        {candidates.map((c) => (
          <option key={c.ref} value={c.ref}>
            {c.name}
          </option>
        ))}
      </Select>
      {chosen ? (
        <Select
          aria-label={`JSON column for ${chosen.name}`}
          className="h-6 w-48"
          value=""
          data-testid="store-whole-json-column"
          onChange={(e) => {
            const column = jsonColumns.find((c) => c.key === e.target.value);
            if (column) {
              onPick(chosen.ref, column);
              setRef("");
            }
          }}
        >
          <option value="">{jsonColumns.length ? "Pick a JSON column" : "No free JSON column in the table"}</option>
          {jsonColumns.map((c) => (
            <option key={c.key} value={c.key}>
              {c.name}
            </option>
          ))}
        </Select>
      ) : null}
    </div>
  );
}
