// The inspector on the Databases side (the Database screen, the Databases explorer, a table's editor): one subject at a time,
// the TABLE (by its resolved key) or one of its parts. The table: its physical fields (name, schema, comment, description,
// category, stereotypes, tags), its custom properties and its property bag, edited in its file; a table the model lays out is
// stored as a table file on its first edit (one undo step). A part (a column, the primary key, a unique constraint, an index,
// a foreign key or a check) shows alone under a breadcrumb whose table part goes back to the table: a column's physical fields,
// tags, stereotypes and property bag (written through the column grid's path, useTableFile), a key's or an index's members
// (written through the table editor's path, useTableDoc). JSON: the table file, or the resolved table, read-only, until it is
// stored. The database side knows tables only.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ExternalLink, PanelTop, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { useDatabaseView, useElements, useProject } from "@/api/queries";
import type { ColumnView, Diagnostic, ModelJson, StereotypeDoc, TableView } from "@/api/types";
import { CodeView } from "@/code";
import { clone } from "@/lib/json";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { EmptyState, SectionTitle, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { KindIcon } from "@/app/icons";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { BUILTIN_TYPES } from "@/model/model";
import { schemasOf } from "@/model/databaseSchemas";
import { USED_LABEL } from "@/model/labels";
import {
  columnEntryOf,
  columnText,
  emptyOverlay,
  physicalHint,
  tableKeyOfDoc,
  type ColumnEditField,
  type ColumnField,
  type ColumnValue,
} from "@/workspaces/database/columnEdits";
import { useTableFile } from "@/workspaces/database/useTableFile";
import { useTableDoc, type TableDoc } from "@/workspaces/database/useTableDoc";
import { shownTable, storeTables, tableIdentity } from "@/workspaces/database/storeTables";
import { openTableEditor } from "@/workspaces/database/openTableEditor";
import { openEditForeignKey } from "@/workspaces/database/foreignKeys";
import {
  findPart,
  isLaidOutKey,
  PART_LABELS,
  partCrumb,
  partEntries,
  partName,
  qualifiedTable,
  nullsNotDistinctNote,
  onDeleteColumnsNote,
  resolvedTableDoc,
  setsColumns,
  type TablePart,
} from "@/workspaces/database/tableParts";
import { tableColumns } from "@/editors/database/databaseDocs";
import { CommitInput } from "@/editors/database/fields";
import {
  ActionSelect,
  CheckColumnSelect,
  ColumnsPicker,
  DeferrableSelect,
  deferrableNote,
  IndexColumnDetails,
  IndexColumns,
  MethodSelect,
  partProblems,
  ReferencedColumns,
  ReferencedTableSelect,
  useDialect,
  usePartEdits,
} from "@/editors/database/TablePartsTabs";
import { useProblems } from "@/editors/process/shared";
import { ColumnFacets, columnProblems } from "@/editors/database/ColumnFacets";
import { ChipsEditor, CommonFields, setOptional, TextField, useVocabularies, type FormProps } from "./fields";
import { useDraftDocument } from "./useDraft";
import { applicableExtensions, SchemaForm } from "./SchemaForm";
import { ElementPropertyBag, PropertyBag } from "./PropertyBag";
import { declaredKeys } from "./propertyBag";
import { DeleteButton, JsonTab, References, statusBadge } from "./Inspector";

type Rec = Record<string, unknown>;
type TableTab = "properties" | "json" | "references";

const TAB_LABELS: Record<TableTab, string> = { properties: "Properties", json: "JSON", references: USED_LABEL };

export function TableInspector({
  database,
  tableKey,
  column,
  part = null,
}: {
  database: string;
  tableKey: string;
  column: string | null;
  part?: TablePart | null;
}) {
  const view = useDatabaseView(database);
  // A table stored as a file a moment ago is shown by the key it was stored from until the view lists the file, in the same
  // inspector throughout (a field being typed when the store lands keeps its text).
  const table = useMemo(() => shownTable(view.data?.view?.tables, tableKey), [view.data, tableKey]);
  if (view.isPending) return <Spinner label="Resolving the table" />;
  if (!table)
    return (
      <EmptyState title="This table is not in the database">
        {view.data && !view.data.view ? "The model has errors, so its tables cannot be resolved." : "It was removed, or its database changed."}
      </EmptyState>
    );
  return (
    <TableInspectorBody
      key={`${database}|${tableIdentity(tableKey)}`}
      database={database}
      table={table}
      tables={view.data?.view?.tables ?? []}
      subject={column ? { kind: "column", id: column } : part}
      problems={view.data?.stale ? view.data.diagnostics.filter((d) => d.severity === "error") : []}
    />
  );
}

function TableInspectorBody({
  database,
  table,
  tables,
  subject,
  problems,
}: {
  database: string;
  table: TableView;
  tables: TableView[];
  /** The part shown alone; null: the table. */
  subject: TablePart | null;
  /** The model's errors while the view is the last resolved one (stale). */
  problems: Diagnostic[];
}) {
  const { store } = useServices();
  const { openTable } = useEditorNavigation();
  const file = useTableFile(table, database);
  // A table stored on its first edit has no draft until it is a file; one that adjusts a laid-out table edits that file.
  const draft = useDraftDocument(file.fileId && !file.owner ? file.fileId : null);
  const doc = useTableDocument(table, database, file, draft);
  const td = useTableDoc(database, table.key);
  const fileProblems = useProblems(td.fileId ?? "", td.diagnostics);
  const [tab, setTab] = useState<TableTab>("properties");
  const tabs: TableTab[] = ["properties", "json", "references"];
  const laidOut = isLaidOutKey(table.key);
  const column = subject?.kind === "column" ? (table.columns.find((c) => c.key === subject.id) ?? null) : null;
  const part = subject && subject.kind !== "column" ? subject : null;
  const partShown = part && td.doc ? partName(td.doc, part, table) : part?.id;
  const subjectName = column ? column.name : partShown;
  const fileJson = laidOut ? undefined : (draft.json as unknown as Rec | undefined);

  return (
    <section aria-label={`Inspector: table ${table.name}`} className="flex h-full min-h-0 flex-col bg-surface" data-testid="table-inspector">
      <header className="flex flex-col gap-1 border-b border-default px-2 py-1">
        {subject ? (
          <nav
            aria-label={`Breadcrumb: ${partCrumb(table, subjectName ?? null)}`}
            className="flex min-w-0 items-center gap-1 text-12"
            data-testid="inspector-breadcrumb"
          >
            <button
              type="button"
              className="truncate font-mono text-accent hover:underline"
              title="Back to the table"
              aria-label={`Back to the table ${qualifiedTable(table)}`}
              onClick={() => store.getState().inspectPart(null)}
              data-testid="inspector-back-to-table"
            >
              {qualifiedTable(table)}
            </button>
            <span className="text-secondary" aria-hidden>
              ›
            </span>
            <span className="truncate font-mono" aria-current="true">
              {subjectName}
            </span>
          </nav>
        ) : null}
        <div className="flex items-center gap-2">
          <KindIcon kind="table" />
          <h2 className="min-w-0 flex-1 truncate text-14 font-semibold" data-testid="inspector-title">
            {subject ? subjectName : table.name}
          </h2>
          {file.fileId && !laidOut ? <span data-testid="save-status">{statusBadge(draft.draft?.status, file.busy)}</span> : null}
          <Button
            size="icon-sm"
            variant="ghost"
            label="Open the table editor"
            onClick={() => openTableEditor(store, database, table.key, { part: subject })}
            data-testid="table-inspector-edit"
          >
            <PanelTop />
          </Button>
          <Button
            size="icon-sm"
            variant="ghost"
            label="Open in the Database screen"
            onClick={() => openTable(database, table.key, column?.key ?? null)}
            data-testid="table-inspector-open"
          >
            <ExternalLink />
          </Button>
          {!subject && !laidOut && draft.json ? <DeleteButton id={table.key} name={table.name} /> : null}
        </div>
        {subject ? null : (
          <p className="truncate font-mono text-11 text-secondary" data-testid="table-inspector-path">
            {laidOut ? `${qualifiedTable(table)} · not stored as a table file yet` : (draft.element.data?.path ?? qualifiedTable(table))}
          </p>
        )}
        {problems.length ? (
          <ul role="alert" className="flex flex-col gap-0.5 text-12 text-danger" data-testid="table-inspector-problems">
            {(problems.some((d) => d.elementId === table.key) ? problems.filter((d) => d.elementId === table.key) : problems).slice(0, 3).map((d, i) => (
              <li key={i}>
                <span className="font-mono">{d.rule}</span> {d.message}
              </li>
            ))}
          </ul>
        ) : null}
      </header>
      {column ? (
        <div className="min-h-0 flex-1 overflow-auto p-2">
          <ColumnSection
            key={column.key}
            database={database}
            table={table}
            tables={tables}
            column={column}
            entry={file.fileJson ? (columnEntryOf(file.fileJson, column) ?? null) : null}
            write={file.write}
            problems={columnProblems(fileProblems, td.doc, column)}
          />
        </div>
      ) : part ? (
        <div className="min-h-0 flex-1 overflow-auto p-2">
          <PartSection key={`${part.kind}:${part.id}`} td={td} part={part} />
        </div>
      ) : (
        <Tabs value={tab} onValueChange={(next) => setTab(next as TableTab)} className="flex min-h-0 flex-1 flex-col">
          <TabsList aria-label="Inspector views">
            {tabs.map((t) => (
              <TabsTrigger key={t} value={t}>
                {TAB_LABELS[t]}
              </TabsTrigger>
            ))}
          </TabsList>
          <TabsContent value="properties" className="overflow-auto p-2">
            <div className="flex flex-col gap-3">
              <p className="text-12 text-secondary" data-testid="table-inspector-column-hint">
                Pick a column, a key, an index or a check (the explorer, the column grid, the table editor) to see it here alone.
              </p>
              <TableSection database={database} table={table} doc={doc} />
            </div>
          </TabsContent>
          <TabsContent value="json" className="flex min-h-0 flex-col p-0">
            {laidOut ? (
              <ResolvedJson td={td} />
            ) : fileJson ? (
              <JsonTab json={draft.json!} onChange={(next) => draft.edit(() => next)} />
            ) : (
              <Spinner label="Loading the table's file" />
            )}
          </TabsContent>
          <TabsContent value="references" className="overflow-auto p-2">
            {laidOut ? <EmptyState title="Nothing uses this table's file">It is not stored as a table file yet.</EmptyState> : <References id={table.key} />}
          </TabsContent>
        </Tabs>
      )}
    </section>
  );
}

/** The document the table's own fields edit: its file's draft, or, for a laid-out table, a local document written on commit. */
interface TableDocument {
  /** The file's id, or a stable dom id for the overlay still to create. */
  id: string;
  hasFile: boolean;
  json: ModelJson | undefined;
  form: FormProps | null;
}

/**
 * The table's own document for its fields. With a file: its draft (saved 600 ms after the last keystroke, or on blur).
 * A table the model lays out: a document kept here (its resolved document, or for one that cannot be stored yet the file that
 * adjusts it); edits apply to it at once and are written together on blur, Enter, a pick, or 600 ms after the last keystroke,
 * as one write (storing the table as a file first, or creating the adjusting file), one undo step.
 */
function useTableDocument(
  table: TableView,
  database: string,
  file: ReturnType<typeof useTableFile>,
  draft: ReturnType<typeof useDraftDocument>,
): TableDocument {
  const target = file.target;
  const storable = !!file.owner;
  const base = useMemo(
    () => (target?.kind !== "overlay" ? null : ((storable ? resolvedTableDoc(table, database) : emptyOverlay(target, database, "")) as unknown as ModelJson)),
    [target, database, storable, table],
  );
  const [local, setLocal] = useState<ModelJson | null>(null);
  const current = useRef<ModelJson | null>(null);
  const queued = useRef<((json: ModelJson) => ModelJson | void)[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // The latest writer: useTableFile's functions change with every render, the flush must not.
  const writer = useRef(file.writeTable);
  writer.current = file.writeTable;
  const live = !!file.fileId && !!draft.json;

  const flushLocal = useCallback(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
    const updates = queued.current.splice(0);
    if (!updates.length) return;
    void writer.current((doc) => {
      const identity = { id: doc.id, kind: doc.kind, database: doc.database, origin: doc.origin };
      for (const update of updates) {
        const next = update(doc as unknown as ModelJson) as unknown as Record<string, unknown> | undefined;
        if (next && next !== doc) {
          for (const k of Object.keys(doc)) delete doc[k];
          Object.assign(doc, next, identity);
        }
      }
    });
  }, []);

  // Once the file's own document shows, the local overlay is done with (an undo of the create starts a new one).
  useEffect(() => {
    if (!live) return;
    flushLocal();
    current.current = null;
    setLocal(null);
  }, [live, flushLocal]);
  // Leaving the table writes what was typed.
  useEffect(() => () => flushLocal(), [flushLocal]);

  const editLocal = useCallback(
    (update: (json: ModelJson) => ModelJson | void) => {
      if (!base) return;
      const working = clone(current.current ?? base);
      const next = (update(working) ?? working) as ModelJson;
      current.current = next;
      setLocal(next);
      queued.current.push(update);
      if (timer.current) clearTimeout(timer.current);
      timer.current = setTimeout(flushLocal, 600);
    },
    [base, flushLocal],
  );

  if (live)
    return {
      id: file.fileId!,
      hasFile: true,
      json: draft.json,
      form: {
        id: file.fileId!,
        json: draft.json!,
        doc: draft.element.data,
        edit: draft.edit,
        flush: () => void draft.flush(),
        diagnostics: draft.draft?.diagnostics ?? [],
      },
    };
  const id = `overlay-${table.key}`.replace(/[^A-Za-z0-9_-]/g, "_");
  const json = local ?? base ?? undefined;
  // A file whose document is still loading (and no local edits to show meanwhile) waits.
  if (!json || (!storable && file.fileId && !local) || (!storable && !file.fileId && file.pending && !local))
    return { id, hasFile: !!file.fileId, json: undefined, form: null };
  return { id, hasFile: !!file.fileId, json, form: { id, json, doc: undefined, edit: editLocal, flush: flushLocal, diagnostics: [] } };
}

/** The table's own fields, edited in its file (a laid-out table is stored as one on its first edit). */
function TableSection({ database, table, doc }: { database: string; table: TableView; doc: TableDocument }) {
  const dbDoc = useElements([database]).byId.get(database)?.json as Rec | undefined;
  const project = useProject();
  const vocab = useVocabularies("table");
  const schemas = schemasOf(dbDoc);
  if (!doc.form) return <Spinner label="Loading the table's file" />;
  const props = doc.form;
  const json = props.json as unknown as Rec;
  const stereotypeKeys = ((json.stereotypes as string[] | undefined) ?? []) as string[];
  const extensions = applicableExtensions(project.data?.extensions ?? [], "table", stereotypeKeys);
  const defaults: Record<string, { value: unknown; from: string }> = {};
  for (const key of stereotypeKeys) {
    const st = vocab.allStereotypes.find((x) => x.key === key) as StereotypeDoc | undefined;
    for (const [name, value] of Object.entries((st?.defaultProperties as Record<string, unknown> | undefined) ?? {})) defaults[name] ??= { value, from: key };
  }
  return (
    <div className="flex flex-col gap-2" data-testid="table-inspector-table">
      <SectionTitle>Table</SectionTitle>
      {table.origin === "synthesized" ? null : <ReadOnly label="Origin" value={table.origin} />}
      <CommonFields {...props} />
      <Field label="Schema" htmlFor={`${props.id}-schema`}>
        <Select
          id={`${props.id}-schema`}
          value={String(json.schema ?? "")}
          onChange={(e) => {
            props.edit((j) => setOptional(j as unknown as Rec, "schema", e.target.value));
            props.flush();
          }}
        >
          <option value="">(the database's default{table.schema ? `: ${table.schema}` : ""})</option>
          {schemas.map((sc) => (
            <option key={sc.id} value={sc.id}>
              {sc.name}
            </option>
          ))}
        </Select>
      </Field>
      <TextField
        id={`${props.id}-comment`}
        label="Comment"
        value={String(json.comment ?? "")}
        placeholder={table.comment ?? undefined}
        onChange={(v) => props.edit((j) => setOptional(j as unknown as Rec, "comment", v))}
        onBlur={props.flush}
      />
      {extensions.length ? (
        <div className="flex flex-col gap-2">
          <SectionTitle>Custom properties</SectionTitle>
          <SchemaForm
            idPrefix={`${props.id}-prop`}
            extensions={extensions}
            json={props.json}
            defaults={defaults}
            onChange={(name, value) =>
              props.edit((j) => {
                const record = j as { properties?: Record<string, unknown> };
                const next = { ...(record.properties ?? {}) };
                if (value === undefined) delete next[name];
                else next[name] = value;
                if (Object.keys(next).length) record.properties = next;
                else delete record.properties;
              })
            }
          />
        </div>
      ) : null}
      <ElementPropertyBag id={props.id} json={props.json} edit={props.edit} flush={props.flush} declared={declaredKeys(extensions)} />
    </div>
  );
}

/** A table not stored as a file yet: its resolved document, read-only, and the way to store it (when it can be). */
function ResolvedJson({ td }: { td: TableDoc }) {
  const services = useServices();
  const qc = useQueryClient();
  const [storing, setStoring] = useState(false);
  const text = useMemo(() => JSON.stringify(td.doc ?? {}, null, 2), [td.doc]);
  if (storing) return <Spinner label="Storing the table as a file" />;
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="table-inspector-resolved-json">
      <div className="flex items-center gap-2 border-b border-default px-2 py-1">
        <p className="min-w-0 flex-1 text-12 text-secondary">The table as resolved, read-only: it is not stored as a table file yet.</p>
        {td.mode === "storable" && td.owner && td.table ? (
          <Button
            size="sm"
            data-testid="table-inspector-store-file"
            onClick={() => {
              const name = td.table!.name;
              setStoring(true);
              void storeTables(services, qc, td.database, new Map([[td.key, td.owner!]]), `Store ${name} as a table file`).then((r) => {
                if (r.ok) services.store.getState().notify(`${name} is now stored as a table file.`);
                else setStoring(false);
              });
            }}
          >
            Store as a table file
          </Button>
        ) : null}
      </div>
      <div className="min-h-0 flex-1">
        <CodeView language="json" label="Table JSON" value={text} readOnly />
      </div>
    </div>
  );
}

function ReadOnly({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="grid grid-cols-[6rem_1fr] items-baseline gap-2 text-12">
      <span className="text-secondary">{label}</span>
      <span className={mono ? "truncate font-mono" : "break-words"}>{value || "none"}</span>
    </div>
  );
}

/** The foreign key a column belongs to, and the column it references: the line "follows the referenced column". */
function referencedOf(table: TableView, tables: TableView[], column: ColumnView): { name: string; nativeType: string } | null {
  for (const fk of table.foreignKeys) {
    const i = fk.columns.indexOf(column.key);
    if (i < 0) continue;
    const target = tables.find((t) => t.key === fk.referencedTable);
    const ref = target?.columns.find((c) => c.key === fk.referencedColumns[i]);
    if (target && ref) return { name: `${target.name}.${ref.name}`, nativeType: ref.nativeType };
  }
  return null;
}

/**
 * The picked column's physical fields, tags, stereotypes and property bag, each committed (blur, Enter, a pick) as one write
 * through the grid's path. The bag shows the column entry's own properties (`entry`, from the table's file), never the
 * stereotypes' defaults the resolved column merges in.
 */
function ColumnSection({
  database,
  table,
  tables,
  column,
  entry,
  write,
  problems,
}: {
  database: string;
  table: TableView;
  tables: TableView[];
  column: ColumnView;
  /** The column's own entry in the table's file, or null (no file yet). */
  entry: Rec | null;
  write: (column: ColumnView, field: ColumnEditField, value: ColumnValue) => void;
  /** The column's problems in the table's file (MQ4057 among them). */
  problems: Diagnostic[];
}) {
  const vocab = useVocabularies("column");
  const { store } = useServices();
  const dbView = useDatabaseView(database);
  const id = `col-${table.key}-${column.key}`.replace(/[^A-Za-z0-9_-]/g, "_");
  const hint = physicalHint(column, null);
  const referenced = column.isForeignKey ? referencedOf(table, tables, column) : null;
  const commit = (field: Exclude<ColumnField, "nullable">, value: string) => {
    if (value !== columnText(column, field)) write(column, field, value);
  };
  return (
    <div className="flex flex-col gap-2" data-testid="table-inspector-column">
      <SectionTitle>Column {column.name}</SectionTitle>
      {column.isForeignKey ? (
        <p className="text-12 text-secondary" data-testid="table-inspector-references">
          Follows the referenced column{referenced ? ` ${referenced.name} (${referenced.nativeType})` : ""}; a different type set here is reported (MQ4005).
        </p>
      ) : null}
      <CommitField id={`${id}-name`} label="Name" value={column.name} mono onCommit={(v) => commit("name", v)} />
      <Field label="Type" htmlFor={`${id}-type`} hint={hint}>
        <Select id={`${id}-type`} className="font-mono" value={column.type} onChange={(e) => commit("type", e.target.value)}>
          {(BUILTIN_TYPES as string[]).includes(column.type) ? null : (
            <option value={column.type} disabled>
              {column.type}
            </option>
          )}
          {BUILTIN_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </Select>
      </Field>
      <div className="grid grid-cols-3 gap-2">
        <CommitField id={`${id}-length`} label="Length" value={columnText(column, "length")} mono onCommit={(v) => commit("length", v)} />
        <CommitField id={`${id}-precision`} label="Precision" value={columnText(column, "precision")} mono onCommit={(v) => commit("precision", v)} />
        <CommitField id={`${id}-scale`} label="Scale" value={columnText(column, "scale")} mono onCommit={(v) => commit("scale", v)} />
      </div>
      <CommitField
        id={`${id}-native`}
        label="Native type"
        value={column.nativeType}
        mono
        hint="The database's type: the type map's, or the one typed here (cleared, the type map's again)."
        onCommit={(v) => commit("nativeType", v)}
      />
      <ColumnFacets
        id={id}
        column={column}
        entry={entry}
        problems={problems}
        dialect={dbView.data?.view?.dialect}
        sequences={dbView.data?.view?.sequences ?? []}
        onSet={(field, value) => write(column, field, value)}
      />
      <CheckboxField id={`${id}-nullable`} label="Nullable" checked={column.nullable} onChange={(v) => write(column, "nullable", v)} />
      <CommitField id={`${id}-default`} label="Default" value={columnText(column, "default")} mono onCommit={(v) => commit("default", v)} />
      <CommitField id={`${id}-comment`} label="Comment" value={columnText(column, "comment")} onCommit={(v) => commit("comment", v)} />
      <CommitField id={`${id}-description`} label="Description" value={columnText(column, "description")} long onCommit={(v) => commit("description", v)} />
      <ChipsEditor
        label="Stereotypes"
        values={column.stereotypes ?? []}
        options={vocab.stereotypes.map((st) => ({ value: st.key, label: `«${st.key}»` }))}
        allowFree={false}
        empty="No stereotype applies to a column yet; Settings › Stereotypes declares them."
        onChange={(values) => write(column, "stereotypes", values)}
      />
      <ChipsEditor
        label="Tags"
        values={column.tags ?? []}
        options={vocab.tags}
        allowFree={!vocab.strictTags}
        onChange={(values) => write(column, "tags", values)}
      />
      <PropertyBag idPrefix={id} properties={entry ? entry.properties : column.properties} onEdit={(edit) => write(column, "properties", edit)} />
      <div>
        <Button
          size="sm"
          variant="ghost"
          onClick={() => store.getState().requestPartDelete({ database, key: table.key, part: { kind: "column", id: column.key } })}
          data-testid="table-inspector-delete-column"
        >
          <Trash2 /> Delete column
        </Button>
      </div>
    </div>
  );
}

/**
 * One key, constraint or index alone: its members, each change one save of the table (a laid-out table is stored as a file
 * first, in the same undo step), its problems, and Delete (back to the table).
 */
function PartSection({ td, part }: { td: TableDoc; part: TablePart }) {
  const services = useServices();
  const qc = useQueryClient();
  const edits = usePartEdits(td);
  const dialect = useDialect(td);
  const problems = useProblems(td.fileId ?? "", td.diagnostics);
  if (!td.doc || !td.table) return <Spinner label="Resolving the table" />;
  const doc = td.doc;
  const fixed = td.mode === "fixed";
  const index = part.kind === "primary-key" ? 0 : findPart(doc, td.table, part.kind as "unique", part.id);
  const entry = part.kind === "primary-key" ? (doc.primaryKey as Rec | undefined) : index >= 0 ? partEntries(doc, part.kind as "unique")[index] : undefined;
  if (!entry) return <EmptyState title={`This ${PART_LABELS[part.kind]} is not on the table`}>It was deleted or renamed.</EmptyState>;
  const columns = tableColumns(doc);
  const id = `part-${part.kind}-${part.id}`.replace(/[^A-Za-z0-9_-]/g, "_");
  const mine = partProblems(problems, part.kind, index);
  const fkView =
    part.kind === "foreign-key" ? (td.table.foreignKeys.find((f) => f.name === partName(doc, part, td.table)) ?? td.table.foreignKeys[index]) : undefined;
  return (
    <div className="flex flex-col gap-2" data-testid="table-inspector-part" data-part={part.kind}>
      <SectionTitle>{`${PART_LABELS[part.kind][0].toUpperCase()}${PART_LABELS[part.kind].slice(1)} ${partName(doc, part, td.table)}`}</SectionTitle>
      {fixed ? <p className="text-12 text-secondary">This table&apos;s keys and constraints are set by the model; they cannot be edited here yet.</p> : null}
      {mine.length ? (
        <ul role="alert" className="flex flex-col gap-0.5 text-12 text-danger" data-testid="table-inspector-part-problems">
          {mine.map((d, i) => (
            <li key={i}>
              <span className="font-mono">{d.rule}</span> {d.message}
            </li>
          ))}
        </ul>
      ) : null}
      <Field label="Name" htmlFor={`${id}-name`}>
        <CommitInput
          id={`${id}-name`}
          mono
          value={String(entry.name ?? "")}
          placeholder={partName(doc, part, td.table)}
          onCommit={(v) => (v.trim() && !fixed ? void edits.rename(part, v) : undefined)}
        />
      </Field>
      {part.kind === "check" ? (
        <Field label="Column" htmlFor={`${id}-column`} hint="A column check constrains one column; none: a check on the table.">
          <CheckColumnSelect id={`${id}-column`} part={part} entry={entry} columns={columns} edits={edits} fixed={fixed} />
        </Field>
      ) : null}
      {part.kind === "check" ? (
        [dialect, "*"].map((d) => (
          <Field key={d} label={d === "*" ? "Expression (any dialect)" : `Expression (${d})`} htmlFor={`${id}-expr-${d}`}>
            <CommitInput
              id={`${id}-expr-${d}`}
              mono
              value={((entry.expression as Record<string, string> | undefined) ?? {})[d] ?? ""}
              onCommit={(v) => void edits.setExpression(part, d, v)}
            />
          </Field>
        ))
      ) : part.kind === "index" ? (
        <>
          <Field label="Columns and order">
            <IndexColumns part={part} entry={entry} columns={columns} edits={edits} fixed={fixed} dialect={dialect} />
          </Field>
          <IndexColumnDetails part={part} entry={entry} columns={columns} edits={edits} fixed={fixed} dialect={dialect} />
          <Field label="Include">
            <ColumnsPicker
              label={`Include columns of ${part.id}`}
              options={columns}
              chosen={(entry.include as string[] | undefined) ?? []}
              disabled={fixed}
              onToggle={(c) => void edits.toggleInclude(part, c)}
            />
          </Field>
          <CheckboxField id={`${id}-unique`} label="Unique" checked={entry.unique === true} onChange={(v) => void edits.set(part, "unique", v)} />
          <Field label="Method" htmlFor={`${id}-method`}>
            <MethodSelect id={`${id}-method`} part={part} value={String(entry.method ?? "default")} edits={edits} fixed={fixed} />
          </Field>
          <Field label="Where (a partial index)" htmlFor={`${id}-where`}>
            <CommitInput
              id={`${id}-where`}
              mono
              value={String(entry.where ?? "")}
              placeholder="every row"
              onCommit={(v) => void edits.set(part, "where", v.trim())}
            />
          </Field>
        </>
      ) : (
        <Field label="Columns">
          <ColumnsPicker
            label={`Columns of ${part.id}`}
            options={columns}
            chosen={(entry.columns as string[]) ?? []}
            disabled={fixed}
            onToggle={(c) => void edits.toggleColumn(part, c)}
          />
        </Field>
      )}
      {part.kind === "unique" ? (
        <>
          <CheckboxField
            id={`${id}-nulls-not-distinct`}
            label="Nulls not distinct (two rows with nulls in these columns conflict)"
            checked={entry.nullsNotDistinct === true}
            onChange={(v) => void edits.set(part, "nullsNotDistinct", v)}
          />
          {nullsNotDistinctNote(dialect) ? (
            <p className="text-11 text-secondary" data-testid="unique-nulls-note">
              {nullsNotDistinctNote(dialect)}
            </p>
          ) : null}
        </>
      ) : null}
      {part.kind === "primary-key" ? (
        <CheckboxField id={`${id}-clustered`} label="Clustered" checked={entry.clustered === true} onChange={(v) => void edits.set(part, "clustered", v)} />
      ) : null}
      {part.kind === "foreign-key" ? (
        <>
          <Field label="References table" htmlFor={`${id}-table`}>
            <ReferencedTableSelect id={`${id}-table`} part={part} entry={entry} td={td} edits={edits} fixed={fixed} />
          </Field>
          <Field label="Referenced columns" hint="None picked: the referenced table's primary key.">
            <ReferencedColumns part={part} entry={entry} tables={td.tables} edits={edits} fixed={fixed} />
          </Field>
          <div className="grid grid-cols-2 gap-2">
            <Field label="On delete" htmlFor={`${id}-on-delete`}>
              <ActionSelect id={`${id}-on-delete`} part={part} member="onDelete" value={String(entry.onDelete ?? "no-action")} edits={edits} fixed={fixed} />
            </Field>
            <Field label="On update" htmlFor={`${id}-on-update`}>
              <ActionSelect id={`${id}-on-update`} part={part} member="onUpdate" value={String(entry.onUpdate ?? "no-action")} edits={edits} fixed={fixed} />
            </Field>
          </div>
          {setsColumns(entry.onDelete) && ((entry.columns as string[] | undefined) ?? []).length > 1 ? (
            <Field label="Columns set on delete" hint="None picked: every column of the key. Pick some to keep the others, such as a tenant column.">
              <ColumnsPicker
                label={`Columns ${part.id} sets on delete`}
                options={columns.filter((c) => ((entry.columns as string[] | undefined) ?? []).includes(c.id))}
                chosen={(entry.onDeleteColumns as string[] | undefined) ?? []}
                empty="every column of the key"
                disabled={fixed}
                onToggle={(c) => void edits.toggleOnDeleteColumn(part, c)}
              />
            </Field>
          ) : null}
          {setsColumns(entry.onDelete) && ((entry.onDeleteColumns as string[] | undefined) ?? []).length && onDeleteColumnsNote(dialect) ? (
            <p className="text-11 text-secondary" data-testid="fk-on-delete-columns-note">
              {onDeleteColumnsNote(dialect)}
            </p>
          ) : null}
          <Field label="Deferrable" htmlFor={`${id}-deferrable`} hint="When the key is checked: on each statement, or at commit.">
            <DeferrableSelect
              id={`${id}-deferrable`}
              part={part}
              value={String(entry.deferrable ?? "not-deferrable")}
              edits={edits}
              fixed={fixed}
              dialect={dialect}
            />
          </Field>
          {deferrableNote(dialect) ? (
            <p className="text-11 text-secondary" data-testid="fk-deferrable-note">
              {deferrableNote(dialect)}
            </p>
          ) : null}
        </>
      ) : null}
      <div className="flex gap-1">
        {part.kind === "foreign-key" && fkView ? (
          <Button
            size="sm"
            variant="ghost"
            disabled={fixed}
            onClick={() => void openEditForeignKey(services, qc, td.database, td.tables, td.table!, fkView)}
            data-testid="table-inspector-edit-fk"
          >
            Edit in the dialog…
          </Button>
        ) : null}
        <Button size="sm" variant="ghost" disabled={fixed} onClick={() => edits.remove(part)} data-testid="table-inspector-delete-part">
          <Trash2 /> Delete {PART_LABELS[part.kind]}
        </Button>
      </div>
    </div>
  );
}

/** A text kept locally while typed and committed on blur or Enter (Shift+Enter adds a line to a long one); Escape restores. */
function CommitField({
  id,
  label,
  value,
  onCommit,
  mono,
  long,
  hint,
}: {
  id: string;
  label: string;
  value: string;
  onCommit: (value: string) => void;
  mono?: boolean;
  long?: boolean;
  hint?: string;
}) {
  const [text, setText] = useState(value);
  // The text last committed, until the resolved value comes back: Enter then a blur commits once.
  const committed = useRef<string | null>(null);
  useEffect(() => {
    setText(value);
    committed.current = null;
  }, [value]);
  const commit = () => {
    if (text === value || text === committed.current) return;
    committed.current = text;
    onCommit(text);
  };
  const common = {
    id,
    value: text,
    className: mono ? "font-mono" : undefined,
    onBlur: commit,
  };
  return (
    <Field label={label} htmlFor={id} hint={hint}>
      {long ? (
        <Textarea
          {...common}
          rows={3}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              commit();
            } else if (e.key === "Escape") setText(value);
          }}
        />
      ) : (
        <Input
          {...common}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") commit();
            else if (e.key === "Escape") setText(value);
          }}
        />
      )}
    </Field>
  );
}

/** The header's "Open" for a table document shown as an element (outside the Databases side): the Database screen focused on its table. */
export function OpenTableButton({ json }: { json: unknown }) {
  const { openTable } = useEditorNavigation();
  const target = tableKeyOfDoc(json as Rec | undefined);
  if (!target) return null;
  return (
    <Button size="icon-sm" variant="ghost" label="Open in the Database screen" onClick={() => openTable(target.database, target.key)} data-testid="table-open">
      <ExternalLink />
    </Button>
  );
}
