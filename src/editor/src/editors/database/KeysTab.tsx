// A designed table's Keys tab: its primary key, unique constraints, indexes and foreign keys as dense grids editing the table
// document (schemas/v1/table.json), each with Add and a Delete per row. Every change is one save of the table, so one undo step;
// a key always keeps one column (the schema's minimum), so the last column of a key cannot be unticked.
import type { ReactNode } from "react";
import { ChevronDown, Plus, Trash2 } from "lucide-react";
import { useDatabaseView } from "@/api/queries";
import type { TableView } from "@/api/types";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuLabel, DropdownMenuTrigger } from "@/components/ui/menu";
import { SectionTitle } from "@/components/ui/misc";
import { newId } from "@/lib/ids";
import type { EditorContext } from "../EditorFrame";
import { CommitInput } from "./fields";
import { addKeyEntry, keyEntry, removeKeyEntry, setEntryMember, tableColumns, toggleColumn, type KeySection } from "./databaseDocs";

type Rec = Record<string, unknown>;

const ACTIONS = ["no-action", "restrict", "cascade", "set-null", "set-default"] as const;
const METHODS = ["default", "btree", "hash", "gin", "gist", "clustered"] as const;

const cell = "h-[var(--mq-row-h)] px-1 align-middle";
const head = "h-6 px-1 text-left text-11 font-semibold text-secondary";
const select = "h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12";

export function KeysTab({ ctx }: { ctx: EditorContext }) {
  const doc = ctx.json as unknown as Rec;
  const database = String(doc.database ?? "");
  const view = useDatabaseView(database);
  const tables: TableView[] = view.data?.view?.tables ?? [];
  const own = tableColumns(doc);
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  const entryOf = (j: Rec, section: KeySection, index: number) => keyEntry(j, section, index) as Rec;
  const noColumns = own.length === 0;
  const others = tables.filter((t) => t.key !== ctx.id);
  const firstTarget = others[0]?.key ?? ctx.id;

  const pk = doc.primaryKey as Rec | undefined;
  const uniques = (Array.isArray(doc.uniques) ? doc.uniques : []) as Rec[];
  const indexes = (Array.isArray(doc.indexes) ? doc.indexes : []) as Rec[];
  const fks = (Array.isArray(doc.foreignKeys) ? doc.foreignKeys : []) as Rec[];

  const nameCell = (section: KeySection, index: number, entry: Rec) => (
    <td className={cell}>
      <CommitInput
        label={`Name of ${section === "primaryKey" ? "the primary key" : `row ${index + 1}`}`}
        mono
        className="h-6 text-12"
        value={String(entry.name ?? "")}
        placeholder="by convention"
        onCommit={(v) => update((j) => setEntryMember(entryOf(j, section, index), "name", v.trim()))}
      />
    </td>
  );
  const deleteCell = (section: KeySection, index: number, what: string) => (
    <td className={`${cell} w-7`}>
      <Button size="icon-row" variant="ghost" label={`Delete ${what}`} onClick={() => update((j) => removeKeyEntry(j, section, index))}>
        <Trash2 />
      </Button>
    </td>
  );
  const columnsCell = (section: KeySection, index: number, columns: string[], what: string) => (
    <td className={cell}>
      <ColumnsPicker
        label={`Columns of ${what}`}
        options={own}
        chosen={columns}
        onToggle={(column) =>
          update((j) => {
            const entry = entryOf(j, section, index);
            if (section === "indexes") {
              const current = (entry.columns as Rec[]).map((c) => String(c.column));
              const next = toggleColumn(current, column);
              if (next) entry.columns = next.map((c) => (entry.columns as Rec[]).find((x) => x.column === c) ?? { column: c });
            } else {
              const next = toggleColumn((entry.columns as string[]) ?? [], column);
              if (next) entry.columns = next;
            }
          })
        }
      />
    </td>
  );
  const addButton = (section: KeySection, label: string, disabled = false) => (
    <Button
      size="sm"
      variant="ghost"
      disabled={noColumns || disabled}
      title={noColumns ? "Add a column first (Columns tab)." : undefined}
      onClick={() => update((j) => void addKeyEntry(j, section, newId, firstTarget))}
      data-testid={`keys-add-${section}`}
    >
      <Plus /> {label}
    </Button>
  );

  return (
    <div className="flex flex-col gap-3" data-testid="table-keys">
      <KeySectionView title="Primary key" action={addButton("primaryKey", "Add primary key", !!pk)} empty={pk ? null : "No primary key."}>
        {pk ? (
          <Grid headers={["Name", "Columns", "Clustered", ""]} testid="keys-primary">
            <tr className="border-t border-default">
              {nameCell("primaryKey", 0, pk)}
              {columnsCell("primaryKey", 0, (pk.columns as string[]) ?? [], "the primary key")}
              <td className={`${cell} w-20`}>
                <input
                  type="checkbox"
                  aria-label="Clustered"
                  checked={pk.clustered === true}
                  onChange={(e) => update((j) => setEntryMember(entryOf(j, "primaryKey", 0), "clustered", e.target.checked))}
                />
              </td>
              {deleteCell("primaryKey", 0, "the primary key")}
            </tr>
          </Grid>
        ) : null}
      </KeySectionView>

      <KeySectionView title="Unique constraints" action={addButton("uniques", "Add unique")} empty={uniques.length ? null : "No unique constraint."}>
        {uniques.length ? (
          <Grid headers={["Name", "Columns", ""]} testid="keys-uniques">
            {uniques.map((u, i) => (
              <tr key={String(u.id ?? i)} className="border-t border-default">
                {nameCell("uniques", i, u)}
                {columnsCell("uniques", i, (u.columns as string[]) ?? [], `unique ${i + 1}`)}
                {deleteCell("uniques", i, `unique ${String(u.name ?? i + 1)}`)}
              </tr>
            ))}
          </Grid>
        ) : null}
      </KeySectionView>

      <KeySectionView title="Indexes" action={addButton("indexes", "Add index")} empty={indexes.length ? null : "No index."}>
        {indexes.length ? (
          <Grid headers={["Name", "Columns", "Unique", "Method", "Where", ""]} testid="keys-indexes">
            {indexes.map((ix, i) => (
              <tr key={String(ix.id ?? i)} className="border-t border-default">
                {nameCell("indexes", i, ix)}
                {columnsCell(
                  "indexes",
                  i,
                  ((ix.columns as Rec[]) ?? []).map((c) => String(c.column)),
                  `index ${i + 1}`,
                )}
                <td className={`${cell} w-14`}>
                  <input
                    type="checkbox"
                    aria-label={`Unique index ${i + 1}`}
                    checked={ix.unique === true}
                    onChange={(e) => update((j) => setEntryMember(entryOf(j, "indexes", i), "unique", e.target.checked))}
                  />
                </td>
                <td className={`${cell} w-24`}>
                  <select
                    aria-label={`Method of index ${i + 1}`}
                    className={select}
                    value={String(ix.method ?? "default")}
                    onChange={(e) =>
                      update((j) => setEntryMember(entryOf(j, "indexes", i), "method", e.target.value === "default" ? undefined : e.target.value))
                    }
                  >
                    {METHODS.map((m) => (
                      <option key={m} value={m}>
                        {m}
                      </option>
                    ))}
                  </select>
                </td>
                <td className={cell}>
                  <CommitInput
                    label={`Filter of index ${i + 1}`}
                    mono
                    className="h-6 text-12"
                    value={String(ix.where ?? "")}
                    placeholder="every row"
                    onCommit={(v) => update((j) => setEntryMember(entryOf(j, "indexes", i), "where", v.trim()))}
                  />
                </td>
                {deleteCell("indexes", i, `index ${String(ix.name ?? i + 1)}`)}
              </tr>
            ))}
          </Grid>
        ) : null}
      </KeySectionView>

      <KeySectionView title="Foreign keys" action={addButton("foreignKeys", "Add foreign key")} empty={fks.length ? null : "No foreign key."}>
        {fks.length ? (
          <Grid headers={["Name", "Columns", "References table", "Referenced columns", "On delete", "On update", ""]} testid="keys-foreign">
            {fks.map((fk, i) => {
              const target = tables.find((t) => t.key === fk.referencesTable);
              const refs = (fk.referencesColumns as string[] | undefined) ?? [];
              return (
                <tr key={String(fk.id ?? i)} className="border-t border-default">
                  {nameCell("foreignKeys", i, fk)}
                  {columnsCell("foreignKeys", i, (fk.columns as string[]) ?? [], `foreign key ${i + 1}`)}
                  <td className={`${cell} min-w-32`}>
                    <select
                      aria-label={`Table foreign key ${i + 1} references`}
                      className={select}
                      value={String(fk.referencesTable ?? "")}
                      onChange={(e) =>
                        update((j) => {
                          const entry = entryOf(j, "foreignKeys", i);
                          entry.referencesTable = e.target.value;
                          delete entry.referencesColumns;
                        })
                      }
                    >
                      {target ? null : <option value={String(fk.referencesTable ?? "")}>{String(fk.referencesTable ?? "")}</option>}
                      {tables.map((t) => (
                        <option key={t.key} value={t.key}>
                          {t.schema ? `${t.schema}.${t.name}` : t.name}
                          {t.key === ctx.id ? " (this table)" : ""}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td className={cell}>
                    <ColumnsPicker
                      label={`Referenced columns of foreign key ${i + 1}`}
                      options={(target?.columns ?? []).map((c) => ({ id: c.key, name: c.name }))}
                      chosen={refs}
                      empty="its primary key"
                      onToggle={(column) =>
                        update((j) => {
                          const entry = entryOf(j, "foreignKeys", i);
                          const current = (entry.referencesColumns as string[] | undefined) ?? [];
                          const next = current.includes(column) ? current.filter((c) => c !== column) : [...current, column];
                          setEntryMember(entry, "referencesColumns", next.length ? next : undefined);
                        })
                      }
                    />
                  </td>
                  {(["onDelete", "onUpdate"] as const).map((member) => (
                    <td key={member} className={`${cell} w-28`}>
                      <select
                        aria-label={`${member === "onDelete" ? "On delete" : "On update"} of foreign key ${i + 1}`}
                        className={select}
                        value={String(fk[member] ?? "no-action")}
                        onChange={(e) =>
                          update((j) => setEntryMember(entryOf(j, "foreignKeys", i), member, e.target.value === "no-action" ? undefined : e.target.value))
                        }
                      >
                        {ACTIONS.map((a) => (
                          <option key={a} value={a}>
                            {a.replace("-", " ")}
                          </option>
                        ))}
                      </select>
                    </td>
                  ))}
                  {deleteCell("foreignKeys", i, `foreign key ${String(fk.name ?? i + 1)}`)}
                </tr>
              );
            })}
          </Grid>
        ) : null}
      </KeySectionView>
      {noColumns ? <p className="text-12 text-secondary">Add a column on the Columns tab before adding keys.</p> : null}
    </div>
  );
}

function KeySectionView({ title, action, empty, children }: { title: string; action: ReactNode; empty: string | null; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-1" aria-label={title}>
      <div className="flex items-center gap-2">
        <SectionTitle>{title}</SectionTitle>
        <span className="flex-1" />
        {action}
      </div>
      {empty ? <p className="text-12 text-secondary">{empty}</p> : children}
    </section>
  );
}

function Grid({ headers, testid, children }: { headers: string[]; testid: string; children: ReactNode }) {
  return (
    <table className="w-full border-collapse text-12" data-testid={testid}>
      <thead>
        <tr>
          {headers.map((h, i) => (
            <th key={i} scope="col" className={head}>
              {h || <span className="sr-only">Actions</span>}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>{children}</tbody>
    </table>
  );
}

/** A list of columns picked from a table's columns: a button naming them, a menu of ticks. */
function ColumnsPicker({
  label,
  options,
  chosen,
  onToggle,
  empty = "none",
}: {
  label: string;
  options: { id: string; name: string }[];
  chosen: string[];
  onToggle: (column: string) => void;
  /** What an empty list means. */
  empty?: string;
}) {
  const names = chosen.map((c) => options.find((o) => o.id === c)?.name ?? c);
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button size="sm" variant="ghost" className="h-6 w-full justify-between px-1 font-mono text-12" aria-label={label} title={label}>
          <span className="min-w-0 truncate">{names.join(", ") || <span className="font-sans text-secondary">{empty}</span>}</span>
          <ChevronDown />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start">
        <DropdownMenuLabel>{label}</DropdownMenuLabel>
        {options.length ? (
          options.map((o) => (
            <DropdownMenuCheckboxItem
              key={o.id}
              checked={chosen.includes(o.id)}
              onSelect={(e) => e.preventDefault()}
              onCheckedChange={() => onToggle(o.id)}
              className="font-mono"
            >
              {o.name}
            </DropdownMenuCheckboxItem>
          ))
        ) : (
          <DropdownMenuLabel>No columns</DropdownMenuLabel>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
