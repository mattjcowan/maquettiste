// "Stored in a table" (2026-10-11), under the Storage tab's strategies: per database, the table file that holds this type's
// rows, the column each field fills and the constant columns that tell the type's rows apart in a table several types share.
// Optional: a database with no table keeps its strategy. With one, the checks count the rows as the table's rows (a table
// seed beside them may not repeat a code, a foreign key into the table may name one) and the database's seed data carries
// them. Choosing a table maps the fields to columns of the same name; each change is one save of the type.
import { loadElement, useElements, useIndex } from "@/api/queries";
import type { ReferenceTypeDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { useDraftDocument } from "@/inspector/useDraft";
import { newId } from "@/lib/ids";
import { Plus, X } from "lucide-react";
import { useRef } from "react";

interface BindingField {
  attribute: string;
  column: string;
}
interface BindingConstant {
  column: string;
  value?: string | number | boolean | null;
}
export interface TypeBinding {
  id: string;
  database: string;
  table: string;
  constants?: BindingConstant[];
  fields?: BindingField[];
}
interface ColumnDoc {
  id: string;
  name: string;
  type?: string;
}
interface TableDoc {
  columns?: ColumnDoc[];
}
type BoundTypeDoc = ReferenceTypeDoc & { bindings?: TypeBinding[] };

const NO_TABLE = "";
const NO_COLUMN = "";

const snake = (name: string) =>
  name
    .replace(/([a-z0-9])([A-Z])/g, "$1_$2")
    .replace(/[^A-Za-z0-9]+/g, "_")
    .toLowerCase();

/** The fields a binding can map: the built-in ones, then the type's single-valued user fields. */
export function bindableFields(type: ReferenceTypeDoc): { key: string; label: string; names: string[] }[] {
  const attributes = ((type.attributes ?? []) as { id: string; name: string; collection?: boolean }[]).filter((a) => !a.collection);
  return [
    { key: "code", label: "Code", names: ["code"] },
    { key: "label", label: "Label", names: ["label", "name"] },
    { key: "description", label: "Description", names: ["description"] },
    ...attributes.map((a) => ({ key: a.id, label: a.name, names: [a.name, snake(a.name)] })),
  ];
}

/** The field map a newly chosen table starts with: each field to the first column named like it. */
export function guessFields(type: ReferenceTypeDoc, columns: ColumnDoc[]): BindingField[] {
  const out: BindingField[] = [];
  const used = new Set<string>();
  for (const field of bindableFields(type)) {
    const column = columns.find((c) => !used.has(c.id) && field.names.some((n) => n.toLowerCase() === c.name.toLowerCase()));
    if (!column) continue;
    used.add(column.id);
    out.push({ attribute: field.key, column: column.id });
  }
  return out;
}

/** A constant's text as the column stores it: a number for a numeric column, true or false for a boolean one. */
export function constantValue(text: string, type: string | undefined): string | number | boolean {
  if (type === "bool" && (text === "true" || text === "false")) return text === "true";
  if (type && /^(int16|int32|int64|decimal|float|double)$/.test(type) && text.trim() !== "" && !Number.isNaN(Number(text))) return Number(text);
  return text;
}

export function TableBindings({ typeId, databases }: { typeId: string; databases: { id: string; name: string }[] }) {
  const { json, edit, flush } = useDraftDocument(typeId);
  const index = useIndex();
  const type = json as unknown as BoundTypeDoc | undefined;
  const bindings = type?.bindings ?? [];
  const tables = useElements(bindings.map((b) => b.table));
  // The latest table choice per database: a slower load of an earlier choice must not overwrite it.
  const choices = useRef(new Map<string, string>());
  if (!type || !databases.length) return null;
  const tablesOf = (databaseId: string) =>
    (index.data ?? []).filter((r) => r.kind === "table" && r.database === databaseId && !r.entity).sort((a, b) => a.name.localeCompare(b.name));

  const change = (mutate: (list: TypeBinding[]) => TypeBinding[]) => {
    edit((j) => {
      const t = j as unknown as BoundTypeDoc;
      const next = mutate(structuredClone(t.bindings ?? []));
      if (next.length) t.bindings = next;
      else delete t.bindings;
    });
    void flush();
  };
  const setTable = async (databaseId: string, tableId: string) => {
    choices.current.set(databaseId, tableId);
    if (tableId === NO_TABLE) {
      change((list) => list.filter((b) => b.database !== databaseId));
      return;
    }
    const columns = ((await loadElement(tableId).catch(() => null))?.json as unknown as TableDoc | undefined)?.columns ?? [];
    if (choices.current.get(databaseId) !== tableId) return;
    change((list) => [
      ...list.filter((b) => b.database !== databaseId),
      { id: list.find((b) => b.database === databaseId)?.id ?? newId(), database: databaseId, table: tableId, fields: guessFields(type, columns) },
    ]);
  };

  return (
    <section className="mt-3 flex flex-col gap-1" aria-label="Stored in a table" data-testid="reference-table-bindings">
      <span className="text-12 font-semibold">Stored in a table</span>
      <p className="text-12 text-secondary">
        Optional. When this type&apos;s rows live in a table of a database, name the table and the column each field fills: the checks then count the rows as
        the table&apos;s rows (a foreign key into it may name a code, a table seed beside them may not repeat one), and the database&apos;s seed data carries
        them. The table wins over the strategy in its database.
      </p>
      <table className="w-full border-collapse text-12" aria-label="Table per database">
        <thead>
          <tr className="bg-app text-left text-11 font-semibold text-secondary">
            <th className="h-7 w-40 px-1.5">Database</th>
            <th className="px-1.5">Table and columns</th>
          </tr>
        </thead>
        <tbody>
          {databases.map((database) => {
            const binding = bindings.find((b) => b.database === database.id);
            const columns = binding ? (((tables.byId.get(binding.table)?.json as unknown as TableDoc | undefined)?.columns ?? []) as ColumnDoc[]) : [];
            return (
              <tr key={database.id} className="border-t border-default align-top" data-testid={`table-binding-${database.name}`}>
                <td className="h-6 px-1 py-1">{database.name}</td>
                <td className="px-1.5 py-1">
                  <Select
                    aria-label={`Table for ${database.name}`}
                    className="h-7 text-12"
                    value={binding?.table ?? NO_TABLE}
                    onChange={(e) => void setTable(database.id, e.target.value)}
                  >
                    <option value={NO_TABLE}>No table (the strategy applies)</option>
                    {tablesOf(database.id).map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.name}
                      </option>
                    ))}
                  </Select>
                  {binding ? <BindingMap type={type} binding={binding} columns={columns} change={change} /> : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </section>
  );
}

function BindingMap({
  type,
  binding,
  columns,
  change,
}: {
  type: ReferenceTypeDoc;
  binding: TypeBinding;
  columns: ColumnDoc[];
  change: (mutate: (list: TypeBinding[]) => TypeBinding[]) => void;
}) {
  const update = (mutate: (b: TypeBinding) => void) =>
    change((list) =>
      list.map((b) => {
        if (b.id !== binding.id) return b;
        mutate(b);
        if (!b.constants?.length) delete b.constants;
        return b;
      }),
    );
  const setField = (field: string, column: string) =>
    update((b) => {
      const rest = (b.fields ?? []).filter((f) => f.attribute !== field);
      b.fields = column === NO_COLUMN ? rest : [...rest, { attribute: field, column }];
    });
  const columnOf = (reference: string) => columns.find((c) => c.id === reference || c.name === reference);
  return (
    <div className="mt-1 flex flex-col gap-0.5" data-testid="binding-fields">
      {bindableFields(type).map((field) => {
        const mapped = binding.fields?.find((f) => f.attribute === field.key);
        return (
          <label key={field.key} className="flex items-center gap-1 text-11 text-secondary">
            <span className="w-28 shrink-0 truncate" title={field.label}>
              {field.label}
              {field.key === "code" ? " *" : ""}
            </span>
            <Select
              aria-label={`Column for ${field.label}`}
              className="h-6 text-12"
              value={mapped ? (columnOf(mapped.column)?.id ?? mapped.column) : NO_COLUMN}
              onChange={(e) => setField(field.key, e.target.value)}
            >
              <option value={NO_COLUMN}>{field.key === "code" ? "Choose the code column" : "Not stored"}</option>
              {columns.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </Select>
          </label>
        );
      })}
      <div className="mt-1 flex items-center gap-1 text-11 text-secondary">
        <span>Constant columns</span>
        <span className="text-11">(a fixed value on every row, to tell this type&apos;s rows apart in a table several types share)</span>
      </div>
      {(binding.constants ?? []).map((constant, i) => {
        const column = columnOf(constant.column);
        return (
          // Keyed by content as well: the value input keeps its own text until blur, so a removal must not hand it to the next row.
          <div key={`${i}:${constant.column}:${String(constant.value)}`} className="flex items-center gap-1" data-testid="binding-constant">
            <Select
              aria-label={`Constant column ${i + 1}`}
              className="h-6 w-40 text-12"
              value={column?.id ?? constant.column}
              onChange={(e) =>
                update((b) => {
                  b.constants![i] = { ...b.constants![i], column: e.target.value };
                })
              }
            >
              {columns.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </Select>
            <Input
              aria-label={`Constant value ${i + 1}`}
              className="h-6 w-40 text-12"
              defaultValue={constant.value === null || constant.value === undefined ? "" : String(constant.value)}
              onBlur={(e) =>
                update((b) => {
                  b.constants![i] = { column: b.constants![i].column, value: constantValue(e.target.value, column?.type) };
                })
              }
            />
            <Button
              variant="ghost"
              size="icon"
              label={`Remove constant ${column?.name ?? constant.column}`}
              onClick={() =>
                update((b) => {
                  b.constants = (b.constants ?? []).filter((_, k) => k !== i);
                })
              }
            >
              <X className="size-3" />
            </Button>
          </div>
        );
      })}
      <div>
        <Button
          size="sm"
          variant="ghost"
          disabled={!columns.length}
          onClick={() =>
            update((b) => {
              b.constants = [...(b.constants ?? []), { column: columns[0].id, value: "" }];
            })
          }
        >
          <Plus className="size-3" /> Add constant
        </Button>
      </div>
    </div>
  );
}
