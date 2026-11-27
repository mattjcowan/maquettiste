// The constants grid of a binding, a child table entry or (as "Rows of this relation") a relation mapping's link table: fixed values
// for columns, one row each.
import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { constantValue, findColumn, setConstants, type Mapped, type SourceColumn } from "./fieldMap";

/** The constants grid of a binding. A relation mapping's "Rows of this relation" reuses it, with its own title and empty text. */
export function ConstantsGrid({
  dom,
  binding,
  columns,
  onEdit,
  title = "Constants",
  emptyText = "None: every row of the source is this entity's.",
  skipPrimaryKey = false,
}: {
  dom: string;
  binding: Mapped;
  columns: SourceColumn[];
  onEdit: (change: (b: Mapped) => void) => void;
  title?: string;
  emptyText?: string;
  /** A new constant starts on the first free column that is not a primary key (a relation's link table does; an entity's source doesn't). */
  skipPrimaryKey?: boolean;
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
            onClick={() => set([...constants, { column: ((skipPrimaryKey ? free.find((c) => !c.isPrimaryKey) : undefined) ?? free[0]).key, value: "" }])}
          >
            <Plus /> Add constant
          </Button>
        }
      >
        {title}
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
        <p className="text-12 text-secondary">{emptyText}</p>
      )}
    </section>
  );
}
