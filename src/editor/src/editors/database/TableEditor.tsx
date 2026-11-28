// The table editor, for every table: a table file by its id. Tabs: General
// (name, schema, comment, marks and properties), Columns (the column grid, with Add column and Delete column), Primary key,
// Unique constraints, Indexes, Foreign keys, Checks (TablePartsTabs.tsx), DDL (the table's create script from the DDL preview),
// References (the foreign keys pointing at the table, and the elements using its file) and JSON. Every gesture is one save of
// the table, so one undo step. The database side shows tables only.
import { useMemo, type ReactNode } from "react";
import { Plus, Trash2 } from "lucide-react";
import { usePacks, usePreview, useProject } from "@/api/queries";
import type { StereotypeDoc } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { EmptyState, SectionTitle, Spinner } from "@/components/ui/misc";
import { CodeView } from "@/code";
import { CommonFields, useVocabularies } from "@/inspector/fields";
import { JsonTab, References } from "@/inspector/Inspector";
import { applicableExtensions, SchemaForm } from "@/inspector/SchemaForm";
import { ElementPropertyBag } from "@/inspector/PropertyBag";
import { declaredKeys } from "@/inspector/propertyBag";
import { ColumnGrid } from "@/workspaces/database/ColumnGrid";
import { ddlPreviewTarget } from "@/workspaces/database/ddlPreview";
import { openTableEditor } from "@/workspaces/database/openTableEditor";
import { incomingForeignKeys, qualifiedTable, TABLE_TABS, type TableTab } from "@/workspaces/database/tableParts";
import { useTableDoc, type TableDoc } from "@/workspaces/database/useTableDoc";
import { domIdOf, EditorLayout, useEditorContext, type EditorContext } from "../EditorFrame";
import { CommentField, DatabaseLine, SchemaField } from "./fields";
import { PartTab, useSelectedPart, usePartEdits } from "./TablePartsTabs";
import { ExclusionsTab, StorageTab } from "./TableOptionsTabs";
import { SeedDataTab } from "@/editors/SeedDataTab";

type Rec = Record<string, unknown>;

export function TableEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "table");
  if (!ctx) return <>{fallback}</>;
  const doc = ctx.json as unknown as Rec;
  return <TableBody ctx={ctx} draft={draft} database={String(doc.database ?? "")} tableKey={id} />;
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
  const tabs: { value: TableTab; content: ReactNode; fill?: boolean }[] = [
    { value: "general", content: <GeneralTab form={form} /> },
    { value: "columns", content: <ColumnsTab td={td} />, fill: true },
    { value: "primary-key", content: <PartTab td={td} kind="primary-key" /> },
    { value: "uniques", content: <PartTab td={td} kind="unique" /> },
    { value: "indexes", content: <PartTab td={td} kind="index" /> },
    { value: "foreign-keys", content: <PartTab td={td} kind="foreign-key" /> },
    { value: "checks", content: <PartTab td={td} kind="check" /> },
    { value: "exclusions", content: <ExclusionsTab td={td} /> },
    { value: "storage", content: <StorageTab td={td} /> },
    // A table file's own rows (table seeds, 2026-10-07).
    { value: "data" as const, content: <SeedDataTab id={ctx.id} />, fill: true },
    { value: "ddl", content: <DdlTab database={database} tableKey={tableKey} />, fill: true },
    { value: "references", content: <ReferencesTab td={td} /> },
    { value: "json", content: <TableJsonTab ctx={ctx} />, fill: true },
  ];
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      translations
      controls={
        <div className="flex items-center gap-2">
          <DatabaseLine json={{ database }} />
        </div>
      }
      tabs={tabs.map((t) => ({ value: t.value, label: TABLE_TABS[t.value], content: t.content, fill: t.fill }))}
    />
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
        <Button size="sm" variant="ghost" onClick={() => void edits.add("column")} data-testid="table-add-column">
          <Plus /> Add column
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={!picked}
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
          <ColumnGrid key={td.table.key} table={td.table} databaseId={td.database} />
        ) : (
          <EmptyState title="No columns yet">Add column adds one; its name, type and the rest are edited in the grid.</EmptyState>
        )
      ) : (
        <EmptyState title="This table is not resolved yet">It shows once its database has resolved it.</EmptyState>
      )}
    </div>
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
  const text = preview.data.files.map((f) => f.text).join("\n");
  // The whole script in data-text: the code view paints only the lines in view, so a check of what the script says reads it here.
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="table-ddl" data-text={text}>
      <p className="border-b border-default px-2 py-1 text-11 text-secondary">
        {table.pack} · {table.unit}
      </p>
      <div className="min-h-0 flex-1">
        <CodeView language="sql" readOnly label="Table DDL" value={text} />
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

/** The table file's JSON (all of it). */
function TableJsonTab({ ctx }: { ctx: EditorContext }) {
  return <JsonTab json={ctx.json} onChange={(next) => ctx.edit(() => next)} />;
}
