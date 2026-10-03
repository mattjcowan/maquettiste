// The inspector on the Databases side (the Database screen, the Databases explorer): the TABLE, by its resolved key, never the
// entity it derives from (the entity shows in the Domain model). Properties: the table's physical fields (name, schema,
// origin, comment, description, category, stereotypes, tags), its custom properties and its property bag, edited in its file
// like any table document; a projected table without a file shows the same fields, and its first edit creates its overlay
// (one undo step). What the table derives from (or is bound to), with a way to the entity; and, when a column is picked in
// the grid, that column's physical fields, tags, stereotypes and property bag, written through the grid's own path
// (useTableFile: the file of a designed or imported table, the overlay of a projected one, created on the first edit), one
// save per commit. JSON: the file, or for a projected table without one the resolved table, read-only, with a way to create
// the file. The attribute's type and length are validation; the column's are storage; they may differ, and nothing compares them.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ExternalLink } from "lucide-react";
import { useDatabaseView, useElements, useIndex, useProject } from "@/api/queries";
import type { ColumnView, Diagnostic, ModelJson, StereotypeDoc, TableView } from "@/api/types";
import { CodeView } from "@/code";
import { clone } from "@/lib/json";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { EmptyState, SectionTitle, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { KindIcon } from "@/app/icons";
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
  type Derivation,
} from "@/workspaces/database/columnEdits";
import { useColumnDerivations, useTableFile } from "@/workspaces/database/useTableFile";
import { ChipsEditor, CommonFields, setOptional, TextField, useVocabularies, type FormProps } from "./fields";
import { useDraftDocument } from "./useDraft";
import { applicableExtensions, SchemaForm } from "./SchemaForm";
import { ElementPropertyBag, PropertyBag } from "./PropertyBag";
import { declaredKeys } from "./propertyBag";
import { DeleteButton, JsonTab, References, statusBadge } from "./Inspector";

type Rec = Record<string, unknown>;
type TableTab = "properties" | "json" | "references";

const TAB_LABELS: Record<TableTab, string> = { properties: "Properties", json: "JSON", references: USED_LABEL };

export function TableInspector({ database, tableKey, column }: { database: string; tableKey: string; column: string | null }) {
  const view = useDatabaseView(database);
  const table = useMemo(() => view.data?.view?.tables.find((t) => t.key === tableKey) ?? null, [view.data, tableKey]);
  if (view.isPending) return <Spinner label="Resolving the table" />;
  if (!table)
    return (
      <EmptyState title="This table is not in the database">
        {view.data && !view.data.view ? "The model has errors, so its tables cannot be resolved." : "It was removed, or its database changed."}
      </EmptyState>
    );
  return (
    <TableInspectorBody
      key={`${database}|${tableKey}`}
      database={database}
      table={table}
      tables={view.data?.view?.tables ?? []}
      column={column}
      problems={view.data?.stale ? view.data.diagnostics.filter((d) => d.severity === "error") : []}
    />
  );
}

function TableInspectorBody({
  database,
  table,
  tables,
  column,
  problems,
}: {
  database: string;
  table: TableView;
  tables: TableView[];
  column: string | null;
  /** The model's errors while the view is the last resolved one (stale). */
  problems: Diagnostic[];
}) {
  const { openTable, openEntity } = useEditorNavigation();
  const file = useTableFile(table, database);
  const derivations = useColumnDerivations(table);
  const draft = useDraftDocument(file.fileId);
  const doc = useTableDocument(table, database, file, draft);
  const index = useIndex();
  const [tab, setTab] = useState<TableTab>("properties");
  const tabs: TableTab[] = ["properties", "json", "references"];
  const picked = column ? (table.columns.find((c) => c.key === column) ?? null) : null;
  const owner = table.entityId ?? table.relationId;
  const ownerRow = owner ? index.data?.find((r) => r.id === owner) : undefined;
  const ownerName = ownerRow ? ownerRow.displayName || ownerRow.name : null;
  const qualified = `${table.schema ? `${table.schema}.` : ""}${table.name}`;

  return (
    <section aria-label={`Inspector: table ${table.name}`} className="flex h-full min-h-0 flex-col bg-surface" data-testid="table-inspector">
      <header className="flex flex-col gap-1 border-b border-default px-2 py-1">
        <div className="flex items-center gap-2">
          <KindIcon kind="table" />
          <h2 className="min-w-0 flex-1 truncate text-14 font-semibold" data-testid="inspector-title">
            {table.name}
          </h2>
          {file.fileId ? <span data-testid="save-status">{statusBadge(draft.draft?.status, file.busy)}</span> : null}
          <Button
            size="icon-sm"
            variant="ghost"
            label="Open in the Database screen"
            onClick={() => openTable(database, table.key, column)}
            data-testid="table-inspector-open"
          >
            <ExternalLink />
          </Button>
          {file.fileId && draft.json ? <DeleteButton id={file.fileId} name={table.name} /> : null}
        </div>
        <p className="truncate font-mono text-11 text-secondary" data-testid="table-inspector-path">
          {draft.element.data?.path ?? `${qualified} · no file`}
        </p>
        {problems.length ? (
          <ul role="alert" className="flex flex-col gap-0.5 text-12 text-danger" data-testid="table-inspector-problems">
            {(problems.some((d) => d.elementId === file.fileId) ? problems.filter((d) => d.elementId === file.fileId) : problems).slice(0, 3).map((d, i) => (
              <li key={i}>
                <span className="font-mono">{d.rule}</span> {d.message}
              </li>
            ))}
          </ul>
        ) : null}
      </header>
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
            <OwnerLine table={table} ownerName={ownerName} onGo={owner ? () => openEntity(owner) : undefined} />
            {picked ? (
              <ColumnSection
                key={picked.key}
                table={table}
                tables={tables}
                column={picked}
                derivation={derivations.get(picked.key) ?? null}
                entry={file.fileJson ? (columnEntryOf(file.fileJson, picked) ?? null) : null}
                write={file.write}
                onGo={openEntity}
              />
            ) : (
              <p className="text-12 text-secondary" data-testid="table-inspector-column-hint">
                Pick a column in the Columns grid to see and edit its fields here.
              </p>
            )}
            <TableSection database={database} table={table} doc={doc} ownerName={ownerName} />
          </div>
        </TabsContent>
        <TabsContent value="json" className="flex min-h-0 flex-col p-0">
          {file.fileId ? (
            draft.json ? (
              <JsonTab json={draft.json} onChange={(next) => draft.edit(() => next)} />
            ) : (
              <Spinner label="Loading the table's file" />
            )
          ) : (
            <ResolvedJson table={table} create={() => file.writeTable(() => undefined, { force: true })} />
          )}
        </TabsContent>
        <TabsContent value="references" className="overflow-auto p-2">
          {file.fileId ? (
            <References id={file.fileId} />
          ) : (
            <EmptyState title="Nothing references this table">A projected table without a file has no id for other elements to use.</EmptyState>
          )}
        </TabsContent>
      </Tabs>
    </section>
  );
}

/** "Projected from entity X", "Bound to entity X", "Junction of relationship X", with a way to it in the Domain model. */
function OwnerLine({ table, ownerName, onGo }: { table: TableView; ownerName: string | null; onGo?: () => void }) {
  if (!onGo || !ownerName) {
    return (
      <p className="text-12 text-secondary" data-testid="table-inspector-owner">
        {table.origin === "synthesized" ? "Projected table." : "Bound to no entity."}
      </p>
    );
  }
  const text = table.isJunction
    ? `Junction of relationship ${ownerName}`
    : table.origin === "synthesized"
      ? `Projected from entity ${ownerName}`
      : `Bound to entity ${ownerName}`;
  return (
    <div className="flex items-center gap-2 text-12" data-testid="table-inspector-owner">
      <span className="min-w-0 flex-1 truncate">{text}</span>
      <Button size="sm" variant="link" className="h-6 px-0" onClick={onGo} title={`Open ${ownerName} in the Domain model`}>
        {table.isJunction ? "Go to relationship" : "Go to entity"}
      </Button>
    </div>
  );
}

/** The document the table's own fields edit: its file's draft, or, for a projected table without a file, a local overlay. */
interface TableDocument {
  /** The file's id, or a stable dom id for the overlay still to create. */
  id: string;
  hasFile: boolean;
  json: ModelJson | undefined;
  form: FormProps | null;
}

/**
 * The table's own document for its fields. With a file: its draft (saved 600 ms after the last keystroke, or on blur).
 * Without one (a projected table): an overlay kept here; edits apply to it at once and are written together on blur, Enter,
 * a pick, or 600 ms after the last keystroke, as one write of the table's file, the first of which creates the overlay (one
 * undo step). The local overlay shows until the file's own document is loaded.
 */
function useTableDocument(
  table: TableView,
  database: string,
  file: ReturnType<typeof useTableFile>,
  draft: ReturnType<typeof useDraftDocument>,
): TableDocument {
  const target = file.target;
  const base = useMemo(() => (target?.kind === "overlay" ? (emptyOverlay(target, database, "") as unknown as ModelJson) : null), [target, database]);
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
  if (!json || (file.fileId && !local) || (!file.fileId && file.pending && !local)) return { id, hasFile: !!file.fileId, json: undefined, form: null };
  return { id, hasFile: !!file.fileId, json, form: { id, json, doc: undefined, edit: editLocal, flush: flushLocal, diagnostics: [] } };
}

/** The table's own fields: edited in its file, or in the overlay a projected table without one gets on its first edit. */
function TableSection({ database, table, doc, ownerName }: { database: string; table: TableView; doc: TableDocument; ownerName: string | null }) {
  const dbDoc = useElements([database]).byId.get(database)?.json as Rec | undefined;
  const project = useProject();
  const vocab = useVocabularies("table");
  const schemas = schemasOf(dbDoc);
  const origin = table.origin === "synthesized" ? "projected" : table.origin;
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
      {doc.hasFile ? null : (
        <p className="text-12 text-secondary" data-testid="table-inspector-no-file">
          {ownerName ? `Projected from ${ownerName}` : "Projected table"}: edits go to the table's overlay file.
        </p>
      )}
      <ReadOnly label="Origin" value={json.origin === "synthesized" && doc.hasFile ? "projected, with an overlay file" : origin} />
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

/** A projected table without a file: the resolved table as read-only JSON, and the way to create its file (the overlay). */
function ResolvedJson({ table, create }: { table: TableView; create: () => Promise<boolean> }) {
  const [creating, setCreating] = useState(false);
  const text = useMemo(() => JSON.stringify(table, null, 2), [table]);
  if (creating) return <Spinner label="Creating the table's file" />;
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="table-inspector-resolved-json">
      <div className="flex items-center gap-2 border-b border-default px-2 py-1">
        <p className="min-w-0 flex-1 text-12 text-secondary">The resolved table, read-only: this projected table has no file yet.</p>
        <Button
          size="sm"
          data-testid="table-inspector-create-file"
          onClick={() => {
            setCreating(true);
            void create().then((ok) => {
              if (!ok) setCreating(false);
            });
          }}
        >
          Create the table's file
        </Button>
      </div>
      <div className="min-h-0 flex-1">
        <CodeView language="json" label="Resolved table JSON" value={text} readOnly />
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
  table,
  tables,
  column,
  derivation,
  entry,
  write,
  onGo,
}: {
  table: TableView;
  tables: TableView[];
  column: ColumnView;
  derivation: Derivation | null;
  /** The column's own entry in the table's file, or null (no file, or an overlay that does not change the column). */
  entry: Rec | null;
  write: (column: ColumnView, field: ColumnEditField, value: ColumnValue) => void;
  onGo: (id: string) => boolean;
}) {
  const vocab = useVocabularies("column");
  const id = `col-${table.key}-${column.key}`.replace(/[^A-Za-z0-9_-]/g, "_");
  const hint = physicalHint(column, derivation);
  const referenced = column.isForeignKey ? referencedOf(table, tables, column) : null;
  const commit = (field: Exclude<ColumnField, "nullable">, value: string) => {
    if (value !== columnText(column, field)) write(column, field, value);
  };
  return (
    <div className="flex flex-col gap-2" data-testid="table-inspector-column">
      <SectionTitle>Column {column.name}</SectionTitle>
      {derivation ? (
        <div className="flex items-center gap-2 text-12" data-testid="table-inspector-derived">
          <span className="min-w-0 flex-1">
            Derived from attribute <span className="font-mono">{derivation.label}</span>
            {derivation.logical ? <span className="font-mono text-secondary"> ({derivation.logical})</span> : null}
          </span>
          <Button size="sm" variant="link" className="h-6 px-0" onClick={() => onGo(derivation.entityId)} title="Open the entity in the Domain model">
            Go to entity
          </Button>
        </div>
      ) : column.isForeignKey ? (
        <p className="text-12 text-secondary" data-testid="table-inspector-derived">
          Follows the referenced column{referenced ? ` ${referenced.name} (${referenced.nativeType})` : ""}; a different type set here is reported (MQ4005).
        </p>
      ) : (
        <p className="text-12 text-secondary" data-testid="table-inspector-derived">
          Derives from no attribute.
        </p>
      )}
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
