// The table editor's part tabs: Primary key, Unique constraints, Indexes, Foreign keys and Checks, each a dense grid of its
// parts with Add and a Delete per row, editing the table document (schemas/v1/table.json) through `useTableDoc`: every change is
// one save of the table, so one undo step (a table the model lays out is stored as a table file first, in the same step). A row
// picked here is the inspector's subject; the problems of a row show under it. A key keeps one column (the schema's minimum),
// so the last column of a key cannot be unticked.
import { useEffect, useRef, useState, type ReactNode } from "react";
import { ArrowDown, ArrowUp, ChevronDown, Plus, Trash2 } from "lucide-react";
import type { Diagnostic, TableView } from "@/api/types";
import { useDatabaseView } from "@/api/queries";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuLabel, DropdownMenuTrigger } from "@/components/ui/menu";
import { newId } from "@/lib/ids";
import { cn } from "@/lib/cn";
import { useEditor } from "@/state/store";
import { useProblems } from "@/editors/process/shared";
import { tableKeyOf } from "@/search/engine";
import type { TableDoc } from "@/workspaces/database/useTableDoc";
import {
  addIndexExpression,
  addPart,
  editPart,
  findPart,
  indexColumnNote,
  indexColumnText,
  indexLengthProblem,
  keepOnDeleteColumns,
  moveIndexColumn,
  nullsNotDistinctNote,
  PART_LABELS,
  PART_MEMBER,
  partEntries,
  partIdAt,
  qualifiedTable,
  removeIndexColumnAt,
  renamePart,
  setIndexColumnLength,
  setIndexColumnOrder,
  setForeignKeyAction,
  setIndexExpression,
  toggleIndexColumn,
  toggleOnDeleteColumn,
  type ListPartKind,
  type TablePart,
  type TablePartKind,
} from "@/workspaces/database/tableParts";
import { deferrableNote, FK_DEFERRABLE_LABELS } from "@/workspaces/database/fkEdits";
import { openNewForeignKey, useForeignKeyPattern } from "@/workspaces/database/foreignKeys";
import { renameTableColumn } from "@/workspaces/database/columnRename";
import { useQueryClient } from "@tanstack/react-query";
import { CommitInput } from "./fields";
import { setDialectText, setEntryMember, tableColumns, toggleColumn } from "./databaseDocs";

type Rec = Record<string, unknown>;

export { deferrableNote, NO_DEFERRABLE_DIALECTS } from "@/workspaces/database/fkEdits";
export const FK_ACTIONS = ["no-action", "restrict", "cascade", "set-null", "set-default"] as const;
/** When a foreign key is checked (`deferrable`), with its words. */
export const FK_DEFERRABLE = FK_DEFERRABLE_LABELS;
export const INDEX_METHODS = ["default", "btree", "hash", "gin", "gist", "clustered"] as const;

const cell = "h-[var(--mq-row-h)] px-1 align-middle";
const head = "h-6 px-1 text-left text-11 font-semibold text-secondary";
export const selectClass = "h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12";

/** The part the inspector shows for this table (the table editor's selection). */
export function useSelectedPart(database: string, key: string): TablePart | null {
  const { store } = useServices();
  const column = useEditor(store, (s) => (s.inspectedTable?.database === database && s.inspectedTable.key === key ? s.inspectedTable.column : null));
  const part = useEditor(store, (s) => (s.inspectedTable?.database === database && s.inspectedTable.key === key ? (s.inspectedTable.part ?? null) : null));
  return column ? { kind: "column", id: column } : part;
}

/** Picks a part of a table: the inspector shows it alone (with the way back to the table). */
export function useSelectPart(database: string, key: string): (part: TablePart | null) => void {
  const { store } = useServices();
  return (part) => {
    const s = store.getState();
    if (s.inspectedTable?.database === database && s.inspectedTable.key === key) s.inspectPart(part);
    else
      s.inspectTable(
        { database, key, column: part?.kind === "column" ? part.id : null, part: part?.kind === "column" ? null : part },
        tableKeyOf(database, key),
      );
  };
}

/**
 * The edits every part view makes (the tabs' grids and the inspector's part sections): each one change of the table document,
 * labelled for the undo history; a new part is picked at once.
 */
export function usePartEdits(td: TableDoc) {
  const services = useServices();
  const { store } = services;
  const qc = useQueryClient();
  const select = useSelectPart(td.database, td.key);
  const name = td.table?.name ?? String(td.doc?.name ?? "table");
  const entryOf = (doc: Rec, part: TablePart): Rec | undefined => {
    if (part.kind === "primary-key") return doc.primaryKey as Rec | undefined;
    if (part.kind === "column") return undefined;
    const at = findPart(doc, td.table, part.kind, part.id);
    return at < 0 ? undefined : partEntries(doc, part.kind)[at];
  };
  // A part the table no longer has, or a change that changes nothing, is refused: nothing is written (and a table not stored as a
  // file yet is not stored for it).
  const edit = (label: string, part: TablePart, change: (entry: Rec, doc: Rec) => void) => td.update(label, (doc) => editPart(doc, entryOf(doc, part), change));
  return {
    add: async (kind: TablePartKind, referencesTable?: string, dialect?: string) => {
      let made: TablePart | null = null;
      const ok = await td.update(`New ${PART_LABELS[kind]} on ${name}`, (doc) => {
        made = addPart(doc, kind, newId, { referencesTable, dialect });
        return made ? undefined : false;
      });
      if (ok && made) select(made);
      return ok;
    },
    /** Asks to delete a part: its plan (what else goes with it) first, then one batch and one undo step (PartDeleteDialog). */
    remove: (part: TablePart) => store.getState().requestPartDelete({ database: td.database, key: td.key, part }),
    rename: async (part: TablePart, next: string) => {
      // A column's rename rewrites what names it too (columnRename.ts).
      if (part.kind === "column") {
        const done = await renameTableColumn(services, qc, { database: td.database, key: td.key, column: part.id, to: next });
        if (done !== null) return done;
      }
      let renamed: TablePart | null = null;
      const ok = await td.update(`Rename ${PART_LABELS[part.kind]} on ${name}`, (doc) => {
        renamed = renamePart(doc, part, next, td.table);
        return renamed ? undefined : false;
      });
      if (ok && renamed) select(renamed);
      return ok;
    },
    /** Ticks or unticks a column of a key, an index or a foreign key (a list keeps one column). */
    toggleColumn: (part: TablePart, column: string) =>
      edit(`Edit ${PART_LABELS[part.kind]} on ${name}`, part, (entry) => {
        if (part.kind === "index") toggleIndexColumn(entry, column);
        else {
          const next = toggleColumn((entry.columns as string[]) ?? [], column);
          if (next) entry.columns = next;
          if (part.kind === "foreign-key") keepOnDeleteColumns(entry);
        }
      }),
    set: (part: TablePart, member: string, value: unknown) =>
      edit(`Edit ${PART_LABELS[part.kind]} on ${name}`, part, (entry) => setEntryMember(entry, member, value)),
    /** A foreign key's on-delete or on-update action (an on-delete that sets no columns drops the columns it set). */
    setAction: (part: TablePart, member: "onDelete" | "onUpdate", action: string) =>
      edit(`Edit foreign key on ${name}`, part, (entry) => setForeignKeyAction(entry, member, action)),
    /** Ticks or unticks a column of a foreign key among those its on-delete sets. */
    toggleOnDeleteColumn: (part: TablePart, column: string) => edit(`Edit foreign key on ${name}`, part, (entry) => toggleOnDeleteColumn(entry, column)),
    /** A foreign key's referenced table: its referenced columns go back to the table's primary key. */
    setReferences: (part: TablePart, table: string) =>
      edit(`Edit foreign key on ${name}`, part, (entry) => {
        entry.referencesTable = table;
        delete entry.referencesColumns;
      }),
    toggleReferenced: (part: TablePart, column: string) =>
      edit(`Edit foreign key on ${name}`, part, (entry) => {
        const current = (entry.referencesColumns as string[] | undefined) ?? [];
        const next = current.includes(column) ? current.filter((c) => c !== column) : [...current, column];
        setEntryMember(entry, "referencesColumns", next.length ? next : undefined);
      }),
    /** An index column's sort order, by its column id or its place in the index. */
    setOrder: (part: TablePart, column: string | number, descending: boolean) =>
      edit(`Edit index on ${name}`, part, (entry) => setIndexColumnOrder(entry, column, descending)),
    move: (part: TablePart, column: string | number, by: -1 | 1) => edit(`Edit index on ${name}`, part, (entry) => void moveIndexColumn(entry, column, by)),
    /** Adds an expression to an index (a functional index), for the database's dialect, starting from `lower(<first column>)`. */
    addExpression: (part: TablePart, dialect: string, text: string) =>
      edit(`Edit index on ${name}`, part, (entry) => void addIndexExpression(entry, dialect, text)),
    /** An index expression's text for one dialect ("*": any); an empty text removes it unless it is the last one. */
    setIndexExpression: (part: TablePart, at: number, dialect: string, text: string) =>
      edit(`Edit index on ${name}`, part, (entry) => void setIndexExpression(entry, at, dialect, text)),
    /** An index column's key prefix length (MySQL); undefined clears it. */
    setIndexLength: (part: TablePart, at: number, length: number | undefined) =>
      edit(`Edit index on ${name}`, part, (entry) => void setIndexColumnLength(entry, at, length)),
    removeIndexColumn: (part: TablePart, at: number) => edit(`Edit index on ${name}`, part, (entry) => void removeIndexColumnAt(entry, at)),
    toggleInclude: (part: TablePart, column: string) =>
      edit(`Edit index on ${name}`, part, (entry) => {
        const current = (entry.include as string[] | undefined) ?? [];
        const next = current.includes(column) ? current.filter((c) => c !== column) : [...current, column];
        setEntryMember(entry, "include", next.length ? next : undefined);
      }),
    /** A check's expression for one dialect ("*": any); an empty text removes it unless it is the last one. */
    setExpression: (part: TablePart, dialect: string, text: string) =>
      edit(`Edit check on ${name}`, part, (entry) => {
        const map = { ...((entry.expression as Record<string, string> | undefined) ?? {}) };
        if (text.trim()) setDialectText(entry, "expression", dialect, text.trim());
        else if (Object.keys(map).filter((k) => k !== dialect).length) {
          delete map[dialect];
          entry.expression = map;
        }
      }),
  };
}

export type PartEdits = ReturnType<typeof usePartEdits>;

/** The table's problems for one part (by its place in the document), from the last save or the model's validation. */
export function partProblems(problems: readonly Diagnostic[], kind: TablePartKind, index: number): Diagnostic[] {
  const prefix = kind === "primary-key" ? "/primaryKey" : kind === "column" ? `/columns/${index}` : `/${PART_MEMBER[kind as ListPartKind]}/${index}`;
  return problems.filter((d) => d.jsonPointer === prefix || (d.jsonPointer ?? "").startsWith(`${prefix}/`));
}

/** One of the part tabs. */
export function PartTab({ td, kind }: { td: TableDoc; kind: Exclude<TablePartKind, "column"> }) {
  const problems = useProblems(td.fileId ?? "", td.diagnostics);
  const selected = useSelectedPart(td.database, td.key);
  const select = useSelectPart(td.database, td.key);
  const edits = usePartEdits(td);
  const dbDialect = useDialect(td);
  const services = useServices();
  const fkPattern = useForeignKeyPattern(td.database || null);
  if (!td.doc || !td.table) return <p className="text-12 text-secondary">Resolving the table…</p>;
  const doc = td.doc;
  const fixed = td.mode === "fixed";
  const columns = tableColumns(doc);
  const label = { "primary-key": "Add primary key", unique: "Add unique constraint", index: "Add index", "foreign-key": "Add foreign key", check: "Add check" }[
    kind
  ];
  const add = (
    <Button
      size="sm"
      variant="ghost"
      disabled={fixed || !columns.length || (kind === "primary-key" && !!doc.primaryKey)}
      title={!columns.length ? "Add a column first (Columns tab)." : undefined}
      onClick={() =>
        kind === "foreign-key" ? openNewForeignKey(services, td.database, td.tables, td.key, fkPattern) : void edits.add(kind, undefined, dbDialect)
      }
      data-testid={`table-add-${kind}`}
    >
      <Plus /> {label}
    </Button>
  );
  const common = { td, doc, edits, selected, select, problems, fixed };
  return (
    <div className="flex flex-col gap-2" data-testid={`table-tab-${kind}`}>
      <div className="flex items-center gap-2">
        <span className="min-w-0 flex-1 truncate text-12 text-secondary">
          {fixed
            ? "This table's keys and constraints are set by the model; they cannot be edited here yet."
            : `${qualifiedTable(td.table)}: pick a row to see it alone in the inspector.`}
        </span>
        {add}
      </div>
      {kind === "primary-key" ? (
        <PrimaryKeyGrid {...common} />
      ) : kind === "unique" ? (
        <UniquesGrid {...common} dialect={dbDialect} />
      ) : kind === "index" ? (
        <IndexesGrid {...common} dialect={dbDialect} />
      ) : kind === "foreign-key" ? (
        <ForeignKeysGrid {...common} dialect={dbDialect} />
      ) : (
        <ChecksGrid {...common} dialect={dbDialect} />
      )}
    </div>
  );
}

/** The table's database dialect (a check's expression is written for it). */
export function useDialect(td: Pick<TableDoc, "database">): string {
  return useDatabaseView(td.database || null).data?.view?.dialect ?? "postgresql";
}

interface GridProps {
  td: TableDoc;
  doc: Rec;
  edits: PartEdits;
  selected: TablePart | null;
  select: (part: TablePart | null) => void;
  problems: Diagnostic[];
  fixed: boolean;
}

const isPicked = (selected: TablePart | null, part: TablePart) => !!selected && selected.kind === part.kind && selected.id === part.id;

/** A grid row that picks its part, scrolled into view when the part is picked elsewhere (the explorer). */
function PartRow({
  part,
  selected,
  select,
  problems,
  columns,
  children,
  details,
}: {
  part: TablePart;
  selected: TablePart | null;
  select: (p: TablePart) => void;
  problems: Diagnostic[];
  columns: number;
  children: ReactNode;
  /** More of the part, under its row (an index's columns in detail while it is picked). */
  details?: ReactNode;
}) {
  const picked = isPicked(selected, part);
  const ref = useRef<HTMLTableRowElement>(null);
  useEffect(() => {
    if (picked) ref.current?.scrollIntoView({ block: "nearest" });
  }, [picked]);
  return (
    <>
      <tr
        ref={ref}
        className={cn("border-t border-default", picked && "bg-accent-subtle")}
        aria-selected={picked}
        onClick={() => select(part)}
        data-testid={`part-row-${part.kind}-${part.id}`}
      >
        {children}
      </tr>
      {details ? (
        <tr>
          <td colSpan={columns} className="px-1 pb-1">
            {details}
          </td>
        </tr>
      ) : null}
      {problems.length ? (
        <tr>
          <td colSpan={columns} className="px-1 pb-1">
            <ul role="alert" className="flex flex-col text-11 text-danger" data-testid={`part-problems-${part.kind}-${part.id}`}>
              {problems.map((d, i) => (
                <li key={i}>
                  <span className="font-mono">{d.rule}</span> {d.message}
                </li>
              ))}
            </ul>
          </td>
        </tr>
      ) : null}
    </>
  );
}

function NameCell({ part, value, placeholder, edits, fixed }: { part: TablePart; value: string; placeholder?: string; edits: PartEdits; fixed: boolean }) {
  return (
    <td className={cell}>
      <CommitInput
        label={`Name of ${PART_LABELS[part.kind]} ${part.id}`}
        mono
        className="h-6 text-12"
        value={value}
        placeholder={placeholder ?? "by convention"}
        onCommit={(v) => (v.trim() && !fixed ? void edits.rename(part, v) : undefined)}
      />
    </td>
  );
}

function DeleteCell({ part, name, edits, fixed }: { part: TablePart; name: string; edits: PartEdits; fixed: boolean }) {
  return (
    <td className={`${cell} w-7`}>
      <Button size="icon-row" variant="ghost" disabled={fixed} label={`Delete ${PART_LABELS[part.kind]} ${name}`} onClick={() => edits.remove(part)}>
        <Trash2 />
      </Button>
    </td>
  );
}

export function Grid({ headers, testid, children }: { headers: string[]; testid: string; children: ReactNode }) {
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

const Empty = ({ text }: { text: string }) => <p className="text-12 text-secondary">{text}</p>;

function PrimaryKeyGrid({ td, doc, edits, selected, select, problems, fixed }: GridProps) {
  const pk = doc.primaryKey as Rec | undefined;
  if (!pk) return <Empty text="No primary key." />;
  const part: TablePart = { kind: "primary-key", id: "primary-key" };
  const name = String(pk.name ?? td.table?.primaryKey?.name ?? "");
  return (
    <Grid headers={["Name", "Columns", "Clustered", ""]} testid="keys-primary">
      <PartRow part={part} selected={selected} select={select} problems={partProblems(problems, "primary-key", 0)} columns={4}>
        <NameCell part={part} value={String(pk.name ?? "")} placeholder={td.table?.primaryKey?.name} edits={edits} fixed={fixed} />
        <td className={cell}>
          <ColumnsPicker
            label="Columns of the primary key"
            options={tableColumns(doc)}
            chosen={(pk.columns as string[]) ?? []}
            disabled={fixed}
            onToggle={(c) => void edits.toggleColumn(part, c)}
          />
        </td>
        <td className={`${cell} w-20`}>
          <input
            type="checkbox"
            aria-label="Clustered"
            disabled={fixed}
            checked={pk.clustered === true}
            onChange={(e) => void edits.set(part, "clustered", e.target.checked)}
          />
        </td>
        <DeleteCell part={part} name={name} edits={edits} fixed={fixed} />
      </PartRow>
    </Grid>
  );
}

function UniquesGrid({ td, doc, edits, selected, select, problems, fixed, dialect }: GridProps & { dialect: string }) {
  const entries = partEntries(doc, "unique");
  if (!entries.length) return <Empty text="No unique constraint." />;
  return (
    <Grid headers={["Name", "Columns", "Nulls not distinct", ""]} testid="keys-uniques">
      {entries.map((u, i) => {
        const part: TablePart = { kind: "unique", id: partIdAt(doc, td.table, "unique", i) };
        return (
          <PartRow key={String(u.id ?? i)} part={part} selected={selected} select={select} problems={partProblems(problems, "unique", i)} columns={4}>
            <NameCell part={part} value={String(u.name ?? "")} placeholder={td.table?.uniques[i]?.name} edits={edits} fixed={fixed} />
            <td className={cell}>
              <ColumnsPicker
                label={`Columns of ${part.id}`}
                options={tableColumns(doc)}
                chosen={(u.columns as string[]) ?? []}
                disabled={fixed}
                onToggle={(c) => void edits.toggleColumn(part, c)}
              />
            </td>
            <td className={`${cell} w-28`}>
              <NullsNotDistinctBox part={part} entry={u} edits={edits} fixed={fixed} dialect={dialect} />
            </td>
            <DeleteCell part={part} name={part.id} edits={edits} fixed={fixed} />
          </PartRow>
        );
      })}
    </Grid>
  );
}

/** A unique constraint's `nullsNotDistinct` (two rows with nulls conflict); the tooltip says what a dialect without it does. */
export function NullsNotDistinctBox({
  part,
  entry,
  edits,
  fixed,
  dialect,
  id,
}: {
  part: TablePart;
  entry: Rec;
  edits: PartEdits;
  fixed: boolean;
  dialect: string;
  id?: string;
}) {
  const note = nullsNotDistinctNote(dialect);
  return (
    <input
      id={id}
      type="checkbox"
      aria-label={`Nulls not distinct in ${part.id}`}
      title={note ?? "Two rows with nulls in these columns conflict (PostgreSQL 15 and later)."}
      disabled={fixed}
      checked={entry.nullsNotDistinct === true}
      onClick={(e) => e.stopPropagation()}
      onChange={(e) => void edits.set(part, "nullsNotDistinct", e.target.checked)}
    />
  );
}

function IndexesGrid({ td, doc, edits, selected, select, problems, fixed, dialect }: GridProps & { dialect: string }) {
  const entries = partEntries(doc, "index");
  if (!entries.length) return <Empty text="No index." />;
  const columns = tableColumns(doc);
  return (
    <Grid headers={["Name", "Columns and order", "Include", "Unique", "Method", "Where", ""]} testid="keys-indexes">
      {entries.map((ix, i) => {
        const part: TablePart = { kind: "index", id: partIdAt(doc, td.table, "index", i) };
        return (
          <PartRow
            key={String(ix.id ?? i)}
            part={part}
            selected={selected}
            select={select}
            problems={partProblems(problems, "index", i)}
            columns={7}
            details={
              isPicked(selected, part) ? <IndexColumnDetails part={part} entry={ix} columns={columns} edits={edits} fixed={fixed} dialect={dialect} /> : null
            }
          >
            <NameCell part={part} value={String(ix.name ?? "")} placeholder={td.table?.indexes[i]?.name} edits={edits} fixed={fixed} />
            <td className={cell}>
              <IndexColumns part={part} entry={ix} columns={columns} edits={edits} fixed={fixed} dialect={dialect} />
            </td>
            <td className={cell}>
              <ColumnsPicker
                label={`Include columns of ${part.id}`}
                options={columns}
                chosen={(ix.include as string[] | undefined) ?? []}
                disabled={fixed}
                onToggle={(c) => void edits.toggleInclude(part, c)}
              />
            </td>
            <td className={`${cell} w-14`}>
              <input
                type="checkbox"
                aria-label={`Unique index ${part.id}`}
                disabled={fixed}
                checked={ix.unique === true}
                onChange={(e) => void edits.set(part, "unique", e.target.checked)}
              />
            </td>
            <td className={`${cell} w-24`}>
              <MethodSelect part={part} value={String(ix.method ?? "default")} edits={edits} fixed={fixed} />
            </td>
            <td className={cell}>
              <CommitInput
                label={`Filter of index ${part.id}`}
                mono
                className="h-6 text-12"
                value={String(ix.where ?? "")}
                placeholder="every row"
                onCommit={(v) => void edits.set(part, "where", v.trim())}
              />
            </td>
            <DeleteCell part={part} name={part.id} edits={edits} fixed={fixed} />
          </PartRow>
        );
      })}
    </Grid>
  );
}

export function MethodSelect({ part, value, edits, fixed, id }: { part: TablePart; value: string; edits: PartEdits; fixed: boolean; id?: string }) {
  return (
    <select
      id={id}
      aria-label={`Method of index ${part.id}`}
      className={selectClass}
      disabled={fixed}
      value={value}
      onChange={(e) => void edits.set(part, "method", e.target.value === "default" ? undefined : e.target.value)}
    >
      {INDEX_METHODS.map((m) => (
        <option key={m} value={m}>
          {m}
        </option>
      ))}
    </select>
  );
}

/**
 * An index's columns in key order (a column by name, an expression in parentheses), each with its sort order and a move up or
 * down; a menu adds or removes columns, and Add expression adds an expression (a functional index).
 */
export function IndexColumns({
  part,
  entry,
  columns,
  edits,
  fixed,
  dialect,
}: {
  part: TablePart;
  entry: Rec;
  columns: { id: string; name: string }[];
  edits: PartEdits;
  fixed: boolean;
  dialect: string;
}) {
  const list = (entry.columns as Rec[] | undefined) ?? [];
  const nameOf = (id: string) => columns.find((c) => c.id === id)?.name ?? id;
  const chosen = list.map((c) => ({ text: indexColumnText(c, dialect, nameOf), descending: c.descending === true, length: c.length }));
  const first = columns[0]?.name ?? "column";
  return (
    <div className="flex flex-wrap items-center gap-1">
      {chosen.map((c, i) => (
        <span
          key={i}
          className="inline-flex items-center gap-0.5 rounded-control border border-default px-1 font-mono text-12"
          data-testid={`index-column-${c.text}`}
        >
          {c.text}
          {typeof c.length === "number" ? <span className="text-11 text-secondary">({c.length})</span> : null}
          <Button
            size="icon-row"
            variant="ghost"
            disabled={fixed}
            label={c.descending ? `Descending: sort ${c.text} ascending` : `Ascending: sort ${c.text} descending`}
            onClick={(e) => {
              e.stopPropagation();
              void edits.setOrder(part, i, !c.descending);
            }}
          >
            {c.descending ? <ArrowDown /> : <ArrowUp />}
          </Button>
          {i > 0 ? (
            <Button
              size="icon-row"
              variant="ghost"
              disabled={fixed}
              label={`Move ${c.text} earlier`}
              onClick={(e) => {
                e.stopPropagation();
                void edits.move(part, i, -1);
              }}
            >
              <span className="text-11">‹</span>
            </Button>
          ) : null}
          {i < chosen.length - 1 ? (
            <Button
              size="icon-row"
              variant="ghost"
              disabled={fixed}
              label={`Move ${c.text} later`}
              onClick={(e) => {
                e.stopPropagation();
                void edits.move(part, i, 1);
              }}
            >
              <span className="text-11">›</span>
            </Button>
          ) : null}
        </span>
      ))}
      <ColumnsPicker
        label={`Columns of ${part.id}`}
        options={columns}
        chosen={list.filter((c) => typeof c.column === "string").map((c) => String(c.column))}
        disabled={fixed}
        compact
        onToggle={(column) => void edits.toggleColumn(part, column)}
      />
      <Button
        size="icon-row"
        variant="ghost"
        disabled={fixed}
        label={`Add an expression to ${part.id}`}
        onClick={(e) => {
          e.stopPropagation();
          void edits.addExpression(part, dialect, `lower(${first})`);
        }}
        data-testid="index-add-expression"
      >
        <span className="font-mono text-11">ƒx</span>
      </Button>
    </div>
  );
}

/**
 * An index's columns in detail (under its row while it is picked, and in the inspector): each one a column or an expression per
 * dialect (the database's and any dialect, like a check), with its key prefix length (MySQL) and a Remove; what the dialect
 * leaves out is said under them. Each change is one save of the table.
 */
export function IndexColumnDetails({
  part,
  entry,
  columns,
  edits,
  fixed,
  dialect,
}: {
  part: TablePart;
  entry: Rec;
  columns: { id: string; name: string }[];
  edits: PartEdits;
  fixed: boolean;
  dialect: string;
}) {
  const list = (entry.columns as Rec[] | undefined) ?? [];
  const nameOf = (id: string) => columns.find((c) => c.id === id)?.name ?? id;
  const note = indexColumnNote(dialect, {
    expression: list.some((c) => c.expression !== undefined),
    length: list.some((c) => typeof c.length === "number"),
  });
  return (
    <div className="flex flex-col gap-1 rounded-control border border-default p-1" data-testid="index-column-details" onClick={(e) => e.stopPropagation()}>
      <span className="text-11 font-semibold text-secondary">Index columns</span>
      <table className="w-full border-collapse text-12">
        <thead>
          <tr>
            <th scope="col" className={head}>
              Column or expression
            </th>
            <th scope="col" className={`${head} w-28`}>
              Prefix length
            </th>
            <th scope="col" className={`${head} w-7`}>
              <span className="sr-only">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {list.map((c, i) => {
            const isExpression = typeof c.column !== "string";
            const map = (c.expression as Record<string, string> | undefined) ?? {};
            const label = isExpression ? `expression ${i + 1}` : nameOf(String(c.column));
            return (
              <tr key={i} className="border-t border-default" data-testid={`index-detail-${i}`}>
                <td className={cell}>
                  {isExpression ? (
                    <div className="flex flex-col gap-0.5 py-0.5">
                      {[...new Set([dialect, "*", ...Object.keys(map)])].map((d) => (
                        <div key={d} className="grid grid-cols-[6rem_1fr] items-center gap-1">
                          <span className="text-11 text-secondary">{d === "*" ? "any dialect" : d}</span>
                          <CommitInput
                            label={`Expression ${i + 1} of ${part.id} for ${d === "*" ? "any dialect" : d}`}
                            mono
                            className="h-6 text-12"
                            value={map[d] ?? ""}
                            placeholder={d === "*" ? "for every dialect" : "for this dialect"}
                            onCommit={(v) => void edits.setIndexExpression(part, i, d, v)}
                          />
                        </div>
                      ))}
                    </div>
                  ) : (
                    <span className="font-mono">{label}</span>
                  )}
                </td>
                <td className={cell}>
                  <LengthInput part={part} at={i} label={label} value={typeof c.length === "number" ? String(c.length) : ""} edits={edits} fixed={fixed} />
                </td>
                <td className={cell}>
                  <Button
                    size="icon-row"
                    variant="ghost"
                    disabled={fixed || list.length < 2}
                    label={`Remove ${label} from ${part.id}`}
                    onClick={() => void edits.removeIndexColumn(part, i)}
                  >
                    <Trash2 />
                  </Button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {note ? (
        <p className="text-11 text-secondary" data-testid="index-column-note">
          {note}
        </p>
      ) : null}
    </div>
  );
}

/** An index column's key prefix length: a whole number from 1, said inline otherwise (and not saved). */
function LengthInput({
  part,
  at,
  label,
  value,
  edits,
  fixed,
}: {
  part: TablePart;
  at: number;
  label: string;
  value: string;
  edits: PartEdits;
  fixed: boolean;
}) {
  const [problem, setProblem] = useState<string | null>(null);
  return (
    <div className="flex flex-col">
      <CommitInput
        label={`Prefix length of ${label} in ${part.id}`}
        mono
        className="h-6 text-12"
        value={value}
        invalid={!!problem}
        placeholder="whole column"
        onCommit={(v) => {
          const why = indexLengthProblem(v);
          setProblem(why);
          if (!why && !fixed) void edits.setIndexLength(part, at, v.trim() ? Number(v.trim()) : undefined);
        }}
      />
      {problem ? (
        <span role="alert" className="text-11 text-danger" data-testid="index-length-problem">
          {problem}
        </span>
      ) : null}
    </div>
  );
}

function ForeignKeysGrid({ td, doc, edits, selected, select, problems, fixed, dialect }: GridProps & { dialect: string }) {
  const entries = partEntries(doc, "foreign-key");
  if (!entries.length) return <Empty text="No foreign key." />;
  return (
    <Grid headers={["Name", "Columns", "References table", "Referenced columns", "On delete", "On update", "Deferrable", ""]} testid="keys-foreign">
      {entries.map((fk, i) => {
        const part: TablePart = { kind: "foreign-key", id: partIdAt(doc, td.table, "foreign-key", i) };
        return (
          <PartRow key={String(fk.id ?? i)} part={part} selected={selected} select={select} problems={partProblems(problems, "foreign-key", i)} columns={8}>
            <NameCell part={part} value={String(fk.name ?? "")} placeholder={td.table?.foreignKeys[i]?.name} edits={edits} fixed={fixed} />
            <td className={cell}>
              <ColumnsPicker
                label={`Columns of ${part.id}`}
                options={tableColumns(doc)}
                chosen={(fk.columns as string[]) ?? []}
                disabled={fixed}
                onToggle={(c) => void edits.toggleColumn(part, c)}
              />
            </td>
            <td className={`${cell} min-w-32`}>
              <ReferencedTableSelect part={part} entry={fk} td={td} edits={edits} fixed={fixed} />
            </td>
            <td className={cell}>
              <ReferencedColumns part={part} entry={fk} tables={td.tables} edits={edits} fixed={fixed} />
            </td>
            {(["onDelete", "onUpdate"] as const).map((member) => (
              <td key={member} className={`${cell} w-28`}>
                <ActionSelect part={part} member={member} value={String(fk[member] ?? "no-action")} edits={edits} fixed={fixed} />
              </td>
            ))}
            <td className={`${cell} w-36`}>
              <DeferrableSelect part={part} value={String(fk.deferrable ?? "not-deferrable")} edits={edits} fixed={fixed} dialect={dialect} />
            </td>
            <DeleteCell part={part} name={part.id} edits={edits} fixed={fixed} />
          </PartRow>
        );
      })}
    </Grid>
  );
}

export function ActionSelect({
  part,
  member,
  value,
  edits,
  fixed,
  id,
}: {
  part: TablePart;
  member: "onDelete" | "onUpdate";
  value: string;
  edits: PartEdits;
  fixed: boolean;
  id?: string;
}) {
  return (
    <select
      id={id}
      aria-label={`${member === "onDelete" ? "On delete" : "On update"} of ${part.id}`}
      className={selectClass}
      disabled={fixed}
      value={value}
      onChange={(e) => void edits.setAction(part, member, e.target.value)}
    >
      {FK_ACTIONS.map((a) => (
        <option key={a} value={a}>
          {a.replace("-", " ")}
        </option>
      ))}
    </select>
  );
}

/** A foreign key's `deferrable` (not deferrable, the default, is left out of the file); on a dialect without deferrable keys the
 * select says so in its tooltip and the row shows MQ4056. */
export function DeferrableSelect({
  part,
  value,
  edits,
  fixed,
  dialect,
  id,
}: {
  part: TablePart;
  value: string;
  edits: PartEdits;
  fixed: boolean;
  dialect: string;
  id?: string;
}) {
  const note = deferrableNote(dialect);
  return (
    <select
      id={id}
      aria-label={`Deferrable of ${part.id}`}
      title={note}
      className={selectClass}
      disabled={fixed}
      value={value}
      onChange={(e) => void edits.set(part, "deferrable", e.target.value === "not-deferrable" ? undefined : e.target.value)}
    >
      {Object.entries(FK_DEFERRABLE).map(([k, words]) => (
        <option key={k} value={k}>
          {words}
        </option>
      ))}
    </select>
  );
}

/** A check's `column`: the column it constrains (a column check), or none (a table check). */
export function CheckColumnSelect({
  part,
  entry,
  columns,
  edits,
  fixed,
  id,
}: {
  part: TablePart;
  entry: Rec;
  columns: readonly { id: string; name: string }[];
  edits: PartEdits;
  fixed: boolean;
  id?: string;
}) {
  const value = typeof entry.column === "string" ? entry.column : "";
  const known = !value || columns.some((c) => c.id === value);
  return (
    <select
      id={id}
      aria-label={`Column of ${part.id}`}
      className={selectClass}
      disabled={fixed}
      value={value}
      onChange={(e) => void edits.set(part, "column", e.target.value || undefined)}
    >
      <option value="">The table (a table check)</option>
      {known ? null : <option value={value}>{value}</option>}
      {columns.map((c) => (
        <option key={c.id} value={c.id}>
          {c.name}
        </option>
      ))}
    </select>
  );
}

export function ReferencedTableSelect({
  part,
  entry,
  td,
  edits,
  fixed,
  id,
}: {
  part: TablePart;
  entry: Rec;
  td: TableDoc;
  edits: PartEdits;
  fixed: boolean;
  id?: string;
}) {
  const value = String(entry.referencesTable ?? "");
  const known = td.tables.some((t) => t.key === value);
  return (
    <select
      id={id}
      aria-label={`Table ${part.id} references`}
      className={selectClass}
      disabled={fixed}
      value={value}
      onChange={(e) => void edits.setReferences(part, e.target.value)}
    >
      {known ? null : <option value={value}>{value}</option>}
      {td.tables.map((t) => (
        <option key={t.key} value={t.key}>
          {qualifiedTable(t)}
          {t.key === td.key ? " (this table)" : ""}
        </option>
      ))}
    </select>
  );
}

export function ReferencedColumns({
  part,
  entry,
  tables,
  edits,
  fixed,
}: {
  part: TablePart;
  entry: Rec;
  tables: readonly TableView[];
  edits: PartEdits;
  fixed: boolean;
}) {
  const target = tables.find((t) => t.key === entry.referencesTable);
  return (
    <ColumnsPicker
      label={`Referenced columns of ${part.id}`}
      options={(target?.columns ?? []).map((c) => ({ id: c.key, name: c.name }))}
      chosen={(entry.referencesColumns as string[] | undefined) ?? []}
      empty="its primary key"
      disabled={fixed}
      onToggle={(column) => void edits.toggleReferenced(part, column)}
    />
  );
}

function ChecksGrid({ td, doc, edits, selected, select, problems, fixed, dialect }: GridProps & { dialect: string }) {
  const entries = partEntries(doc, "check");
  if (!entries.length)
    return <Empty text={td.mode === "file" ? "No check." : "No check yet: a check is added once the table is stored as a table file (Add check stores it)."} />;
  return (
    <Grid headers={["Name", "Column", `Expression (${dialect})`, "Expression (any dialect)", ""]} testid="keys-checks">
      {entries.map((ck, i) => {
        const part: TablePart = { kind: "check", id: partIdAt(doc, td.table, "check", i) };
        return (
          <PartRow key={String(ck.id ?? i)} part={part} selected={selected} select={select} problems={partProblems(problems, "check", i)} columns={5}>
            <NameCell part={part} value={String(ck.name ?? "")} edits={edits} fixed={fixed} />
            <td className={`${cell} w-36`}>
              <CheckColumnSelect part={part} entry={ck} columns={tableColumns(doc)} edits={edits} fixed={fixed} />
            </td>
            <CheckExpressionCells part={part} entry={ck} dialect={dialect} edits={edits} />
            <DeleteCell part={part} name={part.id} edits={edits} fixed={fixed} />
          </PartRow>
        );
      })}
    </Grid>
  );
}

function CheckExpressionCells({ part, entry, dialect, edits }: { part: TablePart; entry: Rec; dialect: string; edits: PartEdits }) {
  const map = (entry.expression as Record<string, string> | undefined) ?? {};
  return (
    <>
      {[dialect, "*"].map((d) => (
        <td key={d} className={cell}>
          <CommitInput
            label={`Expression of ${part.id} for ${d === "*" ? "any dialect" : d}`}
            mono
            className="h-6 text-12"
            value={map[d] ?? ""}
            placeholder={d === "*" ? "for every dialect" : "for this dialect"}
            onCommit={(v) => void edits.setExpression(part, d, v)}
          />
        </td>
      ))}
    </>
  );
}

/** A list of columns picked from a table's columns: a button naming them, a menu of ticks. */
export function ColumnsPicker({
  label,
  options,
  chosen,
  onToggle,
  empty = "none",
  disabled,
  compact,
}: {
  label: string;
  options: { id: string; name: string }[];
  chosen: string[];
  onToggle: (column: string) => void;
  /** What an empty list means. */
  empty?: string;
  disabled?: boolean;
  /** Only the menu's button (the chosen columns show beside it). */
  compact?: boolean;
}) {
  const names = chosen.map((c) => options.find((o) => o.id === c)?.name ?? c);
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild disabled={disabled}>
        {compact ? (
          <Button size="icon-row" variant="ghost" label={label} onClick={(e) => e.stopPropagation()}>
            <ChevronDown />
          </Button>
        ) : (
          <Button size="sm" variant="ghost" className="h-6 w-full justify-between px-1 font-mono text-12" aria-label={label} title={label}>
            <span className="min-w-0 truncate">{names.join(", ") || <span className="font-sans text-secondary">{empty}</span>}</span>
            <ChevronDown />
          </Button>
        )}
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
