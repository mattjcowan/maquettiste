// The table editor, for every table: a table file by its id, and a table the model lays out by convention by its key (shown
// from its resolved document until its first edit stores it as a table file, after which the tab shows the file). Tabs: General
// (name, schema, comment, marks and properties), Columns (the column grid, with Add column and Delete column), Primary key,
// Unique constraints, Indexes, Foreign keys, Checks (TablePartsTabs.tsx), DDL (the table's create script from the DDL preview),
// References (the foreign keys pointing at the table, and the elements using its file) and JSON. Every gesture is one save of
// the table, so one undo step. The database side shows tables only.
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Database, Plus, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { useDatabaseView, usePacks, usePreview, useProject } from "@/api/queries";
import type { ColumnView, ModelJson, StereotypeDoc } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { EmptyState, SectionTitle, Spinner } from "@/components/ui/misc";
import { CodeView } from "@/code";
import { clone } from "@/lib/json";
import { CommonFields, useVocabularies } from "@/inspector/fields";
import { JsonTab, References } from "@/inspector/Inspector";
import { applicableExtensions, SchemaForm } from "@/inspector/SchemaForm";
import { ElementPropertyBag } from "@/inspector/PropertyBag";
import { declaredKeys } from "@/inspector/propertyBag";
import { ColumnGrid } from "@/workspaces/database/ColumnGrid";
import { columnEntryOf, setColumnField } from "@/workspaces/database/columnEdits";
import { useProblems } from "@/editors/process/shared";
import { newId } from "@/lib/ids";
import { ColumnFacets, columnProblems } from "./ColumnFacets";
import { ddlPreviewTarget } from "@/workspaces/database/ddlPreview";
import { openTableEditor } from "@/workspaces/database/openTableEditor";
import { storeTables } from "@/workspaces/database/storeTables";
import { incomingForeignKeys, isLaidOutKey, qualifiedTable, TABLE_TABS, type TableTab } from "@/workspaces/database/tableParts";
import { useTableDoc, type TableDoc } from "@/workspaces/database/useTableDoc";
import { domIdOf, EditorLayout, useEditorContext, type EditorContext } from "../EditorFrame";
import { CommentField, DatabaseLine, SchemaField } from "./fields";
import { PartTab, useSelectedPart, usePartEdits } from "./TablePartsTabs";

type Rec = Record<string, unknown>;

/** The database a laid-out table's key names (`<owner>@<database>`). */
const databaseOfKey = (key: string) => key.slice(key.lastIndexOf("@") + 1);

export function TableEditor({ id }: { id: string }) {
  if (isLaidOutKey(id)) return <LaidOutTableEditor tableKey={id} />;
  return <FileTableEditor id={id} />;
}

function FileTableEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "table");
  if (!ctx) return <>{fallback}</>;
  const doc = ctx.json as unknown as Rec;
  if (doc.origin === "synthesized") return <AdjustmentsNote doc={doc} />;
  return <TableBody ctx={ctx} draft={draft} database={String(doc.database ?? "")} tableKey={id} />;
}

/** A file that adjusts a table the model lays out: the table itself is what is edited. */
function AdjustmentsNote({ doc }: { doc: Rec }) {
  const { store } = useServices();
  const database = String(doc.database ?? "");
  const owner = typeof doc.relation === "string" ? doc.relation : `${String(doc.entity ?? "")}${typeof doc.attribute === "string" ? `.${doc.attribute}` : ""}`;
  return (
    <EmptyState title="This file adjusts a table">
      <span className="flex flex-col items-center gap-2">
        The table it adjusts is edited in its own editor.
        <Button size="sm" onClick={() => openTableEditor(store, database, `${owner}@${database}`)} data-testid="table-editor-open-table">
          Open the table
        </Button>
      </span>
    </EmptyState>
  );
}

function LaidOutTableEditor({ tableKey }: { tableKey: string }) {
  const database = databaseOfKey(tableKey);
  const td = useTableDoc(database, tableKey);
  const ctx = useLaidOutContext(td);
  if (td.pending) return <Spinner label="Resolving the table" />;
  if (!ctx) return <EmptyState title="This table is not in the database">It was removed, stored as a table file, or its database changed.</EmptyState>;
  return <TableBody ctx={ctx} draft={undefined} database={database} tableKey={tableKey} />;
}

/**
 * The editor context of a table the model lays out: its resolved document, edited locally while typed; a commit (blur, Enter,
 * a pick, or 600 ms after the last keystroke) stores the table as a file and makes the edits on it, one undo step.
 */
function useLaidOutContext(td: TableDoc): EditorContext | null {
  const [local, setLocal] = useState<ModelJson | null>(null);
  const queued = useRef<((json: ModelJson) => ModelJson | void)[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const update = useRef(td.update);
  update.current = td.update;
  const name = td.table?.name ?? "";
  const flush = useCallback(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    const updates = queued.current.splice(0);
    if (!updates.length) return;
    void update.current(`Edit table ${name}`, (doc) => {
      for (const u of updates) {
        const next = u(doc as unknown as ModelJson) as unknown as Rec | undefined;
        if (next && next !== doc) {
          for (const k of Object.keys(doc)) delete doc[k];
          Object.assign(doc, next);
        }
      }
    });
  }, [name]);
  useEffect(() => () => flush(), [flush]);
  const base = td.doc as unknown as ModelJson | undefined;
  const edit = useCallback(
    (fn: (json: ModelJson) => ModelJson | void) => {
      if (!base) return;
      setLocal((current) => {
        const working = clone(current ?? base);
        return (fn(working) ?? working) as ModelJson;
      });
      queued.current.push(fn);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(flush, 600);
    },
    [base, flush],
  );
  const json = local ?? base;
  return useMemo(
    () =>
      json && td.table
        ? { id: td.key, kind: "table" as const, json, doc: undefined, edit, flush, diagnostics: [], summary: undefined, name: td.table.name }
        : null,
    [json, td.table, td.key, edit, flush],
  );
}

function TableBody({
  ctx,
  draft,
  database,
  tableKey,
}: {
  ctx: EditorContext;
  draft: Parameters<typeof EditorLayout>[0]["draft"];
  database: string;
  tableKey: string;
}) {
  const td = useTableDoc(database, tableKey);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const origin = String((ctx.json as unknown as Rec).origin ?? "designed");
  const laidOut = isLaidOutKey(tableKey);
  const tabs: { value: TableTab; content: ReactNode; fill?: boolean }[] = [
    { value: "general", content: <GeneralTab form={form} /> },
    { value: "columns", content: <ColumnsTab td={td} />, fill: true },
    { value: "primary-key", content: <PartTab td={td} kind="primary-key" /> },
    { value: "uniques", content: <PartTab td={td} kind="unique" /> },
    { value: "indexes", content: <PartTab td={td} kind="index" /> },
    { value: "foreign-keys", content: <PartTab td={td} kind="foreign-key" /> },
    { value: "checks", content: <PartTab td={td} kind="check" /> },
    { value: "ddl", content: <DdlTab database={database} tableKey={tableKey} />, fill: true },
    { value: "references", content: <ReferencesTab td={td} /> },
    { value: "json", content: <TableJsonTab td={td} ctx={ctx} />, fill: true },
  ];
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      translations={!laidOut}
      controls={
        <div className="flex items-center gap-2">
          <DatabaseLine json={{ database }} />
          <span className="text-12 text-secondary">
            · {laidOut ? "Not stored as a table file yet: the first edit stores it." : origin === "imported" ? "Imported table" : "Designed table"}
          </span>
          {laidOut ? <StoreButton td={td} /> : null}
        </div>
      }
      tabs={tabs.map((t) => ({ value: t.value, label: TABLE_TABS[t.value], content: t.content, fill: t.fill }))}
    />
  );
}

/** Stores this one laid-out table as a table file (the database screen stores them all at once). */
function StoreButton({ td }: { td: TableDoc }) {
  const services = useServices();
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  if (td.mode !== "storable" || !td.owner || !td.table) return null;
  const name = td.table.name;
  return (
    <Button
      size="sm"
      variant="ghost"
      disabled={busy}
      onClick={() => {
        setBusy(true);
        void storeTables(services, qc, td.database, new Map([[td.key, td.owner!]]), `Store ${name} as a table file`).then((r) => {
          setBusy(false);
          if (r.ok) services.store.getState().notify(`${name} is now stored as a table file.`);
        });
      }}
      data-testid="table-store-file"
    >
      <Database /> Store as a table file
    </Button>
  );
}

function GeneralTab({ form }: { form: EditorContext }) {
  const project = useProject();
  const vocab = useVocabularies("table");
  const json = form.json as unknown as Rec;
  const stereotypeKeys = ((json.stereotypes as string[] | undefined) ?? []) as string[];
  const extensions = applicableExtensions(project.data?.extensions ?? [], "table", stereotypeKeys);
  const defaults: Record<string, { value: unknown; from: string }> = {};
  for (const key of stereotypeKeys) {
    const st = vocab.allStereotypes.find((x) => x.key === key) as StereotypeDoc | undefined;
    for (const [name, value] of Object.entries((st?.defaultProperties as Record<string, unknown> | undefined) ?? {})) defaults[name] ??= { value, from: key };
  }
  return (
    <div className="flex max-w-xl flex-col gap-2" data-testid="table-editor-general">
      <CommonFields {...form} inEditorHeader />
      <SchemaField {...form} />
      <CommentField {...form} />
      {extensions.length ? (
        <div className="flex flex-col gap-2">
          <SectionTitle>Custom properties</SectionTitle>
          <SchemaForm
            idPrefix={`${form.id}-prop`}
            extensions={extensions}
            json={form.json}
            defaults={defaults}
            onChange={(name, value) => {
              form.edit((j) => {
                const record = j as { properties?: Record<string, unknown> };
                const next = { ...(record.properties ?? {}) };
                if (value === undefined) delete next[name];
                else next[name] = value;
                if (Object.keys(next).length) record.properties = next;
                else delete record.properties;
              });
              form.flush();
            }}
          />
        </div>
      ) : null}
      <ElementPropertyBag id={form.id} json={form.json} edit={form.edit} flush={form.flush} declared={declaredKeys(extensions)} />
    </div>
  );
}

/** The table's columns in the column grid (as resolved), with Add column and Delete column. */
function ColumnsTab({ td }: { td: TableDoc }) {
  const selected = useSelectedPart(td.database, td.key);
  const edits = usePartEdits(td);
  const picked = selected?.kind === "column" ? selected.id : null;
  const pickedColumn = picked ? (td.table?.columns.find((c) => c.key === picked) ?? null) : null;
  const pickedName = pickedColumn?.name;
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="table-editor-columns">
      <div className="flex h-7 shrink-0 items-center gap-2 border-b border-default px-2">
        <Button size="sm" variant="ghost" disabled={td.mode === "fixed"} onClick={() => void edits.add("column")} data-testid="table-add-column">
          <Plus /> Add column
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={!picked || td.mode === "fixed"}
          title={picked ? undefined : "Pick a column in the grid first."}
          onClick={() => picked && edits.remove({ kind: "column", id: picked })}
          data-testid="table-delete-column"
        >
          <Trash2 /> {pickedName ? `Delete column ${pickedName}` : "Delete column"}
        </Button>
        <span className="min-w-0 flex-1 truncate text-right text-12 text-secondary">Enter or F2 edits a cell; the inspector shows the picked column.</span>
      </div>
      {td.pending ? (
        <Spinner label="Resolving the table" />
      ) : td.table ? (
        td.table.columns.length ? (
          <>
            <ColumnGrid key={td.table.key} table={td.table} databaseId={td.database} />
            {pickedColumn ? <PickedColumnFacets td={td} column={pickedColumn} /> : null}
          </>
        ) : (
          <EmptyState title="No columns yet">Add column adds one; its name, type and the rest are edited in the grid.</EmptyState>
        )
      ) : (
        <EmptyState title="This table is not resolved yet">It shows once its database has resolved it.</EmptyState>
      )}
    </div>
  );
}

/** The picked column's DDL facets and how it gets its values, under the grid (the grid holds the rest of its fields): each change
 * one save of the table, queued behind the table's other writes. */
function PickedColumnFacets({ td, column }: { td: TableDoc; column: ColumnView }) {
  const problems = useProblems(td.fileId ?? "", td.diagnostics);
  const dbView = useDatabaseView(td.database || null);
  const entry = td.doc ? columnEntryOf(td.doc, column) : undefined;
  const id = `table-col-${column.key}`.replace(/[^A-Za-z0-9_-]/g, "_");
  return (
    <section
      className="max-h-[40%] shrink-0 overflow-auto border-t border-default p-2"
      aria-label={`DDL facets of ${column.name}`}
      data-testid="table-editor-column-facets"
    >
      <SectionTitle>{`Column ${column.name}: DDL`}</SectionTitle>
      <ColumnFacets
        id={id}
        column={column}
        entry={entry}
        problems={columnProblems(problems, td.doc, column)}
        disabled={td.mode === "fixed"}
        dialect={dbView.data?.view?.dialect}
        sequences={dbView.data?.view?.sequences ?? []}
        onSet={(field, value) =>
          void td.update(`Edit column ${column.name} of ${td.table?.name ?? "the table"}`, (doc) => void setColumnField(doc, column, field, value, newId))
        }
      />
    </section>
  );
}

/** The table's create script: the DDL preview's unit for one table. */
function DdlTab({ database, tableKey }: { database: string; tableKey: string }) {
  const packs = usePacks();
  const target = ddlPreviewTarget(packs.data ?? [], database, tableKey, null);
  const table = target?.scope === "table" ? target : null;
  const preview = usePreview(table?.pack ?? "", table?.unit ?? "", table?.elementId ?? null, !!table);
  if (packs.isPending) return <Spinner label="Reading the packs" />;
  if (!table)
    return (
      <EmptyState title="No create script">
        No enabled pack renders one table's script; the Database screen&apos;s DDL preview shows the whole database.
      </EmptyState>
    );
  if (!preview.data) return <Spinner label="Rendering the script" />;
  if (!preview.data.files.length)
    return (
      <ul className="p-2 text-12 text-danger" data-testid="table-ddl-problems">
        {preview.data.diagnostics.map((d, i) => (
          <li key={i}>
            {d.rule} {d.message}
          </li>
        ))}
      </ul>
    );
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="table-ddl">
      <p className="border-b border-default px-2 py-1 text-11 text-secondary">
        {table.pack} · {table.unit}
      </p>
      <div className="min-h-0 flex-1">
        <CodeView language="sql" readOnly label="Table DDL" value={preview.data.files.map((f) => f.text).join("\n")} />
      </div>
    </div>
  );
}

/** The foreign keys of other tables that reference this one, and for a file the elements that use it. */
function ReferencesTab({ td }: { td: TableDoc }) {
  const { store } = useServices();
  const incoming = useMemo(() => incomingForeignKeys(td.tables, td.key), [td.tables, td.key]);
  return (
    <div className="flex flex-col gap-3" data-testid="table-references">
      <section className="flex flex-col gap-1" aria-label="Referenced by">
        <SectionTitle>Referenced by</SectionTitle>
        {incoming.length ? (
          <ul className="flex flex-col text-12">
            {incoming.map((r) => (
              <li key={`${r.table.key}:${r.name}`}>
                <button
                  type="button"
                  className="text-left text-accent hover:underline"
                  onClick={() => openTableEditor(store, td.database, r.table.key, { part: { kind: "foreign-key", id: r.name } })}
                  data-testid={`referenced-by-${r.name}`}
                >
                  <span className="font-mono">{qualifiedTable(r.table)}</span> · {r.name} ({r.columns.join(", ")})
                </button>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-12 text-secondary">No foreign key references this table.</p>
        )}
      </section>
      {td.fileId ? (
        <section className="flex flex-col gap-1" aria-label="Used by">
          <SectionTitle>Used by</SectionTitle>
          <References id={td.fileId} />
        </section>
      ) : null}
    </div>
  );
}

/** The table file's JSON (all of it); a laid-out table's resolved document, read-only, until it is stored. */
function TableJsonTab({ td, ctx }: { td: TableDoc; ctx: EditorContext }) {
  const text = useMemo(() => JSON.stringify(td.doc ?? {}, null, 2), [td.doc]);
  if (td.fileId) return <JsonTab json={ctx.json} onChange={(next) => ctx.edit(() => next)} />;
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="table-json-resolved">
      <div className="flex items-center gap-2 border-b border-default px-2 py-1">
        <p className="min-w-0 flex-1 text-12 text-secondary">The table as resolved, read-only: it is not stored as a table file yet.</p>
        <StoreButton td={td} />
      </div>
      <div className="min-h-0 flex-1">
        <CodeView language="json" label="Table JSON" value={text} readOnly />
      </div>
    </div>
  );
}
