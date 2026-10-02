// The table editor: a table with columns of its own (designed or imported). General (name, schema, comment and the marks),
// Columns (the Database screen's column grid, with Add column and Delete column), Keys (primary key, uniques, indexes, foreign
// keys), Code generation and References. Every gesture is one save of the table file, so one undo step. A projected table's
// overlay file has no editor of its own: it says so and opens the Database screen, where its columns are edited.
import { ExternalLink, Plus, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { invalidateResolved, useDatabaseView } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { CommonFields } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { newId } from "@/lib/ids";
import { useEditor } from "@/state/store";
import { ColumnGrid } from "@/workspaces/database/ColumnGrid";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { addTableColumn, deleteTableColumn } from "./databaseDocs";
import { CommentField, DatabaseLine, SchemaField } from "./fields";
import { KeysTab } from "./KeysTab";

type Rec = Record<string, unknown>;

export const TABLE_EDITOR_TABS = { general: "General", columns: "Columns", keys: "Keys" } as const;

export function TableEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "table");
  if (!ctx) return <>{fallback}</>;
  if ((ctx.json as unknown as Rec).origin === "synthesized") return <OverlayNote ctx={ctx} />;
  return <TableBody ctx={ctx} draft={draft} />;
}

function OverlayNote({ ctx }: { ctx: EditorContext }) {
  const { openTable } = useEditorNavigation();
  const doc = ctx.json as unknown as Rec;
  const database = String(doc.database ?? "");
  const key =
    typeof doc.relation === "string"
      ? `${doc.relation}@${database}`
      : `${String(doc.entity ?? "")}${typeof doc.attribute === "string" ? `.${doc.attribute}` : ""}@${database}`;
  return (
    <EmptyState title="This file adjusts a projected table">
      <span className="flex flex-col items-center gap-2">
        A projected table comes from its entity; its columns are edited in the Database screen&apos;s column grid.
        <Button size="sm" onClick={() => openTable(database, key)} data-testid="table-editor-open-database">
          <ExternalLink /> Open in the Database screen
        </Button>
      </span>
    </EmptyState>
  );
}

function TableBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const origin = String((ctx.json as unknown as Rec).origin ?? "designed");
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex items-center gap-2">
          <DatabaseLine json={ctx.json} />
          <span className="text-12 text-secondary">· {origin === "imported" ? "Imported table" : "Designed table (its own columns)"}</span>
        </div>
      }
      tabs={[
        {
          value: "general",
          label: TABLE_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="table-editor-general">
              <CommonFields {...form} inEditorHeader />
              <SchemaField {...form} />
              <CommentField {...form} />
            </div>
          ),
        },
        { value: "columns", label: TABLE_EDITOR_TABS.columns, content: <ColumnsTab ctx={ctx} />, fill: true },
        { value: "keys", label: TABLE_EDITOR_TABS.keys, content: <KeysTab ctx={ctx} /> },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}

/** The table's columns in the column grid (as resolved), with Add column and Delete column on the table file. */
function ColumnsTab({ ctx }: { ctx: EditorContext }) {
  const { store } = useServices();
  const qc = useQueryClient();
  const doc = ctx.json as unknown as Rec;
  const database = String(doc.database ?? "");
  const view = useDatabaseView(database);
  const table = view.data?.view?.tables.find((t) => t.key === ctx.id) ?? null;
  const picked = useEditor(store, (s) => (s.inspectedTable?.key === ctx.id && s.inspectedTable.database === database ? s.inspectedTable.column : null));
  const pickedName = picked ? table?.columns.find((c) => c.key === picked)?.name : undefined;
  const change = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
    invalidateResolved(qc);
  };
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="table-editor-columns">
      <div className="flex h-7 shrink-0 items-center gap-2 border-b border-default px-2">
        <Button size="sm" variant="ghost" onClick={() => change((j) => void addTableColumn(j, newId))} data-testid="table-add-column">
          <Plus /> Add column
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={!picked}
          title={picked ? undefined : "Pick a column in the grid first."}
          onClick={() => {
            if (!picked) return;
            change((j) => void deleteTableColumn(j, picked));
            store.getState().inspectColumn(null);
          }}
          data-testid="table-delete-column"
        >
          <Trash2 /> {pickedName ? `Delete column ${pickedName}` : "Delete column"}
        </Button>
        <span className="min-w-0 flex-1 truncate text-right text-12 text-secondary">
          {view.data?.stale ? "The model has errors; the columns show as last resolved." : "Enter or F2 edits a cell; the inspector shows the picked column."}
        </span>
      </div>
      {view.isPending ? (
        <Spinner label="Resolving the table" />
      ) : table ? (
        table.columns.length ? (
          <ColumnGrid key={table.key} table={table} databaseId={database} />
        ) : (
          <EmptyState title="No columns yet">Add column adds one; its name, type and the rest are edited in the grid.</EmptyState>
        )
      ) : (
        <EmptyState title="This table is not resolved yet">
          {view.data && !view.data.view ? "The model has errors, so its tables cannot be resolved." : "It shows once its database has resolved it."}
        </EmptyState>
      )}
    </div>
  );
}
