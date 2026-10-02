// The view editor: General (name, schema, comment and the marks), Body (one SQL editor per dialect the body holds, with Add
// dialect for the others; a body is saved when its editor loses focus, on Ctrl+S or with Save, one undo step each), Columns (the
// optional declared columns), Code generation and References.
import { useEffect, useRef, useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { Badge, EmptyState } from "@/components/ui/misc";
import { CodeView } from "@/code";
import { CommonFields, DIALECTS } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { BUILTIN_TYPES } from "@/model/model";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { useElements } from "@/api/queries";
import { ANY_DIALECT, viewBodyTemplate } from "@/explorer/databaseCreate";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { addViewColumn, removeViewColumn, removeViewDialect, setViewBody, viewDialects } from "./databaseDocs";
import { CommentField, CommitInput, DatabaseLine, SchemaField } from "./fields";

type Rec = Record<string, unknown>;

export const VIEW_EDITOR_TABS = { general: "General", body: "Body", columns: "Columns" } as const;

/** A dialect as the Body tab names it. */
export const dialectLabel = (d: string) => (d === ANY_DIALECT ? "Any dialect (*)" : d);

export function ViewEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "view");
  if (!ctx) return <>{fallback}</>;
  return <ViewBody ctx={ctx} draft={draft} />;
}

function ViewBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<DatabaseLine json={ctx.json} />}
      tabs={[
        {
          value: "general",
          label: VIEW_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="view-editor-general">
              <CommonFields {...form} inEditorHeader />
              <SchemaField {...form} />
              <CommentField {...form} />
            </div>
          ),
        },
        { value: "body", label: VIEW_EDITOR_TABS.body, content: <BodyTab ctx={ctx} /> },
        { value: "columns", label: VIEW_EDITOR_TABS.columns, content: <ColumnsTab ctx={ctx} /> },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}

function BodyTab({ ctx }: { ctx: EditorContext }) {
  const doc = ctx.json as unknown as Rec;
  const body = (doc.body as Record<string, string> | undefined) ?? {};
  const dialects = viewDialects(doc);
  const database = String(doc.database ?? "");
  const dbDialect = String((useElements(database ? [database] : []).byId.get(database)?.json as Rec | undefined)?.dialect ?? "");
  const missing = [ANY_DIALECT, ...DIALECTS].filter((d) => !dialects.includes(d));
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  return (
    <div className="flex flex-col gap-2" data-testid="view-body">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          The SELECT the view runs, per dialect; a body for any dialect (*) serves the dialects without one of their own.
          {dbDialect ? ` This database is ${dbDialect}.` : ""}
        </p>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button size="sm" variant="ghost" disabled={!missing.length} data-testid="view-add-dialect">
              <Plus /> Add dialect
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {missing.map((d) => (
              <DropdownMenuItem
                key={d}
                onSelect={() => update((j) => setViewBody(j, d, body[dialects[0] ?? ""] ?? viewBodyTemplate(d)))}
                data-testid={`view-add-dialect-${d}`}
              >
                {dialectLabel(d)}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
      {dialects.map((d) => (
        <BodySection
          key={d}
          viewId={ctx.id}
          dialect={d}
          saved={body[d] ?? ""}
          only={dialects.length === 1}
          onCommit={(text) => update((j) => setViewBody(j, d, text))}
          onRemove={() => update((j) => void removeViewDialect(j, d))}
        />
      ))}
    </div>
  );
}

/** One dialect's body: typed locally (Monaco owns the text while typing) and saved as one edit when it leaves the editor. */
function BodySection({
  viewId,
  dialect,
  saved,
  only,
  onCommit,
  onRemove,
}: {
  viewId: string;
  dialect: string;
  saved: string;
  only: boolean;
  onCommit: (text: string) => void;
  onRemove: () => void;
}) {
  const [text, setText] = useState(saved);
  const [revision, setRevision] = useState(0);
  const textRef = useRef(text);
  textRef.current = text;
  const shown = useRef(saved);
  // The saved body changed elsewhere (undo, another window): the editor shows it.
  useEffect(() => {
    if (saved === shown.current) return;
    shown.current = saved;
    setText(saved);
    setRevision((r) => r + 1);
  }, [saved]);
  const dirty = text !== saved;
  const empty = !text.trim();
  const commit = () => {
    const now = textRef.current;
    if (now === shown.current || !now.trim()) return;
    shown.current = now;
    onCommit(now);
  };
  return (
    <section
      className="flex flex-col rounded-control border border-default"
      aria-label={`Body for ${dialectLabel(dialect)}`}
      data-testid={`view-body-${dialect}`}
    >
      <div className="flex h-7 items-center gap-2 border-b border-default px-2 text-12">
        <span className="font-medium">{dialectLabel(dialect)}</span>
        {dirty ? <Badge tone="accent">Unsaved</Badge> : null}
        {empty ? <span className="text-danger">A body cannot be empty.</span> : null}
        <span className="flex-1" />
        <Button size="sm" variant="ghost" disabled={!dirty || empty} onClick={commit} data-testid={`view-body-save-${dialect}`}>
          Save
        </Button>
        <Button
          size="icon-row"
          variant="ghost"
          label={only ? "A view keeps at least one body" : `Remove the ${dialectLabel(dialect)} body`}
          disabled={only}
          onClick={onRemove}
        >
          <Trash2 />
        </Button>
      </div>
      <div className="h-48">
        <CodeView
          language="sql"
          label={`View body for ${dialectLabel(dialect)}`}
          path={`view-${viewId}-${dialect === ANY_DIALECT ? "any" : dialect}.sql`}
          value={text}
          revision={revision}
          onChange={setText}
          onBlur={commit}
          onSave={commit}
        />
      </div>
    </section>
  );
}

function ColumnsTab({ ctx }: { ctx: EditorContext }) {
  const doc = ctx.json as unknown as Rec;
  const columns = (Array.isArray(doc.columns) ? doc.columns : []) as Rec[];
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  const at = (j: Rec, i: number) => (j.columns as Rec[])[i];
  return (
    <div className="flex flex-col gap-2" data-testid="view-columns">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">Optional: the columns the view returns, for generators that need their names and types.</p>
        <Button size="sm" variant="ghost" onClick={() => update(addViewColumn)} data-testid="view-add-column">
          <Plus /> Add column
        </Button>
      </div>
      {columns.length ? (
        <table className="w-full border-collapse text-12" data-testid="view-columns-grid">
          <thead>
            <tr>
              <th scope="col" className="h-6 px-1 text-left text-11 font-semibold text-secondary">
                Name
              </th>
              <th scope="col" className="h-6 w-36 px-1 text-left text-11 font-semibold text-secondary">
                Type
              </th>
              <th scope="col" className="h-6 w-12 px-1 text-left text-11 font-semibold text-secondary">
                Null
              </th>
              <th scope="col" className="w-7">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {columns.map((c, i) => (
              <tr key={i} className="border-t border-default" data-testid={`view-column-${String(c.name ?? i)}`}>
                <td className="h-[var(--mq-row-h)] px-1">
                  <CommitInput
                    label={`Name of column ${i + 1}`}
                    mono
                    className="h-6 text-12"
                    value={String(c.name ?? "")}
                    onCommit={(v) => {
                      if (v.trim()) update((j) => void (at(j, i).name = v.trim()));
                    }}
                  />
                </td>
                <td className="px-1">
                  <select
                    aria-label={`Type of column ${String(c.name ?? i + 1)}`}
                    className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 font-mono text-12"
                    value={String(c.type ?? "")}
                    onChange={(e) =>
                      update((j) => {
                        if (e.target.value) at(j, i).type = e.target.value;
                        else delete at(j, i).type;
                      })
                    }
                  >
                    <option value="">(not said)</option>
                    {BUILTIN_TYPES.map((t) => (
                      <option key={t} value={t}>
                        {t}
                      </option>
                    ))}
                  </select>
                </td>
                <td className="px-1">
                  <input
                    type="checkbox"
                    aria-label={`Column ${String(c.name ?? i + 1)} accepts nulls`}
                    checked={c.nullable !== false}
                    onChange={(e) =>
                      update((j) => {
                        if (e.target.checked) delete at(j, i).nullable;
                        else at(j, i).nullable = false;
                      })
                    }
                  />
                </td>
                <td className="px-1">
                  <Button
                    size="icon-row"
                    variant="ghost"
                    label={`Delete column ${String(c.name ?? i + 1)}`}
                    onClick={() => update((j) => removeViewColumn(j, i))}
                  >
                    <Trash2 />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <EmptyState title="No declared columns">The view&apos;s columns are what its body selects; declaring them is optional.</EmptyState>
      )}
    </div>
  );
}
