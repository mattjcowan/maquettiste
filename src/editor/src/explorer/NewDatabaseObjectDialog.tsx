// The New dialogs of a database (explorer-redesign.md 1.8): New schema… (DatabaseSchemas.tsx's dialog), New table…, New
// view… and New sequence…. The store's `newDatabaseObject` opens one for a database (and, from a schema row, that schema);
// the explorer's row menus and New menu, the Database screen's New button and the palette ask for it. Create writes the element
// through the create endpoint as one undo step, shows the Databases explorer, selects the element and opens its editor (a
// table in front of the Database screen focused on it).
import { useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { applySaveResult, keys, useDatabaseView, useElements, useIndex } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { DatabaseDoc, ElementSummary, ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { DIALECTS } from "@/inspector/fields";
import { NewSchemaDialog } from "@/inspector/DatabaseSchemas";
import { defaultSchemaName, schemasOf } from "@/model/databaseSchemas";
import { identifierHint } from "@/model/model";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { useEditor, type NewDatabaseObjectRequest } from "@/state/store";
import {
  ANY_DIALECT,
  buildDatabaseObject,
  DATABASE_CREATE_TITLES,
  databaseObjectProblems,
  SEQUENCE_TYPES,
  viewBodyTemplate,
  type DatabaseElementKind,
  type SequenceType,
  type TakenName,
} from "./databaseCreate";

/** The dialogs, mounted once by the app shell. */
export function NewDatabaseObjectHost() {
  const { store } = useServices();
  const request = useEditor(store, (s) => s.newDatabaseObject);
  if (!request) return null;
  const close = () => store.getState().requestNewDatabaseObject(null);
  if (request.kind === "schema") return <NewSchemaDialog database={request.database} onClose={close} />;
  return <NewDatabaseObjectDialog key={`${request.kind}:${request.database}`} request={{ ...request, kind: request.kind }} onClose={close} />;
}

const NAME_HINTS: Record<DatabaseElementKind, string> = {
  table: identifierHint("audit_log", "AuditLog"),
  view: identifierHint("open_invoices"),
  sequence: identifierHint("invoice_number_seq"),
};

function NewDatabaseObjectDialog({ request, onClose }: { request: NewDatabaseObjectRequest & { kind: DatabaseElementKind }; onClose: () => void }) {
  const { store } = useServices();
  const queryClient = useQueryClient();
  const { openDatabase, openEditor, openWorkspace } = useEditorNavigation();
  const { kind, database } = request;
  const dbDoc = useElements([database]).byId.get(database)?.json as (DatabaseDoc & Record<string, unknown>) | undefined;
  const view = useDatabaseView(database);
  const index = useIndex();
  const schemas = schemasOf(dbDoc);
  const defaultName = defaultSchemaName(dbDoc);
  const dialect = String(dbDoc?.dialect ?? "postgresql");
  const [name, setName] = useState("");
  const [schema, setSchema] = useState(() => (request.schema && request.schema !== defaultName ? request.schema : ""));
  const [withIdColumn, setWithIdColumn] = useState(true);
  const [bodyDialect, setBodyDialect] = useState<string | null>(null);
  const shownDialect = bodyDialect ?? dialect;
  const [body, setBody] = useState<string | null>(null);
  const shownBody = body ?? viewBodyTemplate(shownDialect);
  const [type, setType] = useState<SequenceType>("int64");
  const [start, setStart] = useState("1");
  const [increment, setIncrement] = useState("1");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // The schema picker holds a schema's name until the database file has loaded; the document names it by id.
  const schemaId = schemas.find((s) => s.name === schema)?.id ?? null;
  const taken = useMemo<TakenName[]>(() => {
    const resolved = view.data?.view;
    if (resolved) return [...resolved.tables, ...resolved.views, ...resolved.sequences].map((o) => ({ schema: o.schema, name: o.name }));
    return (index.data ?? [])
      .filter((r) => r.database === database && (r.kind === "table" || r.kind === "view" || r.kind === "sequence") && r.name)
      .map((r) => ({ schema: null, name: r.name }));
  }, [view.data, index.data, database]);
  const input = { kind, name, database, schema: schemaId, withIdColumn, dialect: shownDialect, body: shownBody, type, start, increment };
  const problems = databaseObjectProblems(input, taken, schema || null, defaultName);
  const blocked = Object.keys(problems).length > 0 || busy || !dbDoc;
  const title = DATABASE_CREATE_TITLES[kind];

  const create = async () => {
    if (blocked) return;
    setBusy(true);
    try {
      const json = buildDatabaseObject(input, newId);
      const result = await endpoints.createElement(json as unknown as ModelJson);
      if (result.outcome !== "saved") {
        setError(result.diagnostics[0]?.message ?? result.outcome);
        return;
      }
      applySaveResult(queryClient, result);
      const id = String(json.id);
      const s = store.getState();
      s.pushUndo({
        label: `${title} ${name.trim()}`,
        ids: [id],
        before: [null],
        after: [clone((result.current?.json as ModelJson | undefined) ?? (json as unknown as ModelJson))],
        afterHashes: [result.hash],
      });
      onClose();
      const summary =
        queryClient.getQueryData<ElementSummary[]>(keys.index)?.find((r) => r.id === id) ??
        ({ id, kind, name: name.trim(), database, tags: [], stereotypes: [], package: null, category: null } as unknown as ElementSummary);
      s.setSidebar("databases");
      // A view or a sequence opens in front of its database's screen; a table's Open focuses it there itself.
      if (kind !== "table") openDatabase(database);
      openEditor(summary, true);
      s.notify(`Created ${name.trim()}.`);
    } finally {
      setBusy(false);
    }
  };

  const dialects = [dialect, ANY_DIALECT, ...DIALECTS.filter((d) => d !== dialect)];

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={title} description={dbDoc ? `In database ${String(dbDoc.name ?? "")}.` : undefined}>
        <form
          className="flex flex-col gap-2"
          data-testid="new-database-object-dialog"
          data-kind={kind}
          onSubmit={(e) => {
            e.preventDefault();
            void create();
          }}
        >
          <Field label="Name" htmlFor="new-db-name" hint={NAME_HINTS[kind]}>
            <Input
              id="new-db-name"
              autoFocus
              className="font-mono"
              value={name}
              onChange={(e) => setName(e.target.value)}
              aria-invalid={(name !== "" && !!problems.name) || undefined}
            />
          </Field>
          {name !== "" && problems.name ? (
            <p className="text-12 text-danger" data-testid="new-db-name-problem">
              {problems.name}
            </p>
          ) : null}
          <Field label="Schema" htmlFor="new-db-schema">
            <Select id="new-db-schema" value={schema} onChange={(e) => setSchema(e.target.value)}>
              <option value="">{defaultName ? `${defaultName} (default)` : "The default schema"}</option>
              {schemas
                .filter((s) => s.name !== defaultName)
                .map((s) => (
                  <option key={s.id} value={s.name}>
                    {s.name}
                  </option>
                ))}
            </Select>
          </Field>
          {kind === "table" ? (
            <>
              <fieldset className="flex flex-col gap-1" aria-label="Table kind">
                <legend className="text-12 font-medium text-secondary">Kind</legend>
                <label className="flex h-6 items-center gap-2 text-13">
                  <input type="radio" name="new-db-table-kind" checked readOnly />
                  Designed table (its own columns)
                </label>
                <p className="text-11 text-secondary">
                  A projected table comes from mapping an entity to this database, not from here.{" "}
                  <button
                    type="button"
                    className="text-accent underline-offset-2 hover:underline"
                    onClick={() => {
                      onClose();
                      openWorkspace("mappings");
                    }}
                    data-testid="new-db-open-mappings"
                  >
                    Open the Mappings tab
                  </button>
                </p>
              </fieldset>
              <CheckboxField id="new-db-id-column" label="Start with an id column (int64, primary key)" checked={withIdColumn} onChange={setWithIdColumn} />
            </>
          ) : null}
          {kind === "view" ? (
            <>
              <Field label="Dialect" htmlFor="new-db-dialect" hint="The body is SQL for this dialect; the view's editor adds bodies for other dialects.">
                <Select
                  id="new-db-dialect"
                  value={shownDialect}
                  onChange={(e) => {
                    // An untouched body follows the dialect.
                    if (body === null || body === viewBodyTemplate(shownDialect)) setBody(null);
                    setBodyDialect(e.target.value);
                  }}
                >
                  {dialects.map((d) => (
                    <option key={d} value={d}>
                      {d === ANY_DIALECT ? "Any dialect (*)" : d === dialect ? `${d} (the database's)` : d}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Body" htmlFor="new-db-body" hint="The SELECT the view runs.">
                <Textarea id="new-db-body" className="font-mono" rows={6} value={shownBody} onChange={(e) => setBody(e.target.value)} />
              </Field>
              {problems.body ? <p className="text-12 text-danger">{problems.body}</p> : null}
            </>
          ) : null}
          {kind === "sequence" ? (
            <div className="grid grid-cols-3 gap-2">
              <Field label="Type" htmlFor="new-db-type">
                <Select id="new-db-type" value={type} onChange={(e) => setType(e.target.value as SequenceType)}>
                  {SEQUENCE_TYPES.map((t) => (
                    <option key={t} value={t}>
                      {t}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Start" htmlFor="new-db-start">
                <Input
                  id="new-db-start"
                  className="font-mono"
                  inputMode="numeric"
                  value={start}
                  onChange={(e) => setStart(e.target.value)}
                  aria-invalid={!!problems.start || undefined}
                />
              </Field>
              <Field label="Increment" htmlFor="new-db-increment">
                <Input
                  id="new-db-increment"
                  className="font-mono"
                  inputMode="numeric"
                  value={increment}
                  onChange={(e) => setIncrement(e.target.value)}
                  aria-invalid={!!problems.increment || undefined}
                />
              </Field>
            </div>
          ) : null}
          {kind === "sequence" && (problems.start || problems.increment) ? (
            <p className="text-12 text-danger">{[problems.start, problems.increment].filter(Boolean).join(" ")}</p>
          ) : null}
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={blocked} data-testid="new-db-create">
              Create
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
