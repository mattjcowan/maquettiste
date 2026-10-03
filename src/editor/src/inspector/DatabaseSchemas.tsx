// A database's schemas (erratum E26): the Schemas section of the database inspector and the dialogs the explorer's
// New schema… shares. Schema edits are batch operations (add-schema, rename-schema, remove-schema, set-default-schema):
// the server renames the default with its schema and moves what lives in a removed schema to the target. Each is one undo step.
import { useState } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, elementQuery, indexQuery, invalidateResolved, keys, useDatabaseTables, useIndex } from "@/api/queries";
import type { BatchRequest, ModelJson } from "@/api/types";
import { clone } from "@/lib/json";
import { batchUndoEntry } from "@/workspaces/database/tableParts";
import { readDocuments } from "@/workspaces/database/tableBatch";
import { useServices, type AppServices } from "@/app/context";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, Input, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { conventionSchemas, defaultSchemaName, schemasOf, type SchemaOp } from "@/model/databaseSchemas";

/** The undo label of a schema operation. */
function schemaLabel(op: SchemaOp, schemas: readonly { id: string; name: string }[]): string {
  const name = (id: string | undefined) => schemas.find((s) => s.id === id)?.name ?? id ?? "";
  switch (op.op) {
    case "add-schema":
      return `New schema ${op.name}`;
    case "rename-schema":
      return `Rename schema ${name(op.schema)} to ${op.name}`;
    case "remove-schema":
      return `Remove schema ${name(op.schema)}`;
    case "set-default-schema":
      return `Make ${name(op.schema)} the default schema`;
  }
}

/**
 * Runs one schema operation as one undo step: the documents it may change are read first (the database; for a remove, what
 * lives in the database, which may move to the target schema), the batch expects the database as read, and the step restores
 * what the batch changed. Resolves to null when saved, else the reason.
 */
export function useSchemaOperation(): (op: SchemaOp) => Promise<string | null> {
  const qc = useQueryClient();
  const services = useServices();
  return (op) => runSchemaOperation(services, qc, op);
}

/** `useSchemaOperation`'s work: one schema operation, one undo step. */
export async function runSchemaOperation(services: Pick<AppServices, "store" | "drafts">, qc: QueryClient, op: SchemaOp): Promise<string | null> {
  const { store, drafts } = services;
  await drafts.flushAll();
  const database = await qc.fetchQuery({ ...elementQuery(op.id), staleTime: 0 });
  const priors = new Map<string, ModelJson>([[op.id, clone(database.json as ModelJson)]]);
  if (op.op === "remove-schema") {
    const rows = await qc.fetchQuery(indexQuery);
    const ids = rows.filter((r) => r.database === op.id).map((r) => r.id);
    for (const doc of await readDocuments(ids)) priors.set(String(doc.id), clone(doc as unknown as ModelJson));
  }
  const result = await endpoints.applyBatch({ operations: [{ ...op, expectedHash: database.hash }] } as unknown as BatchRequest);
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The operation is not valid.";
  if (result.outcome !== "saved") {
    const failed = result.items.find((i) => i.outcome !== "saved");
    return result.outcome === "conflict" ? "The database changed meanwhile; try again." : (failed?.diagnostics[0]?.message ?? result.outcome);
  }
  applyBatchResult(qc, result);
  store.getState().pushUndo(batchUndoEntry(schemaLabel(op, schemasOf(database.json as unknown as Record<string, unknown>)), priors, result.items));
  for (const item of result.items) if (item.id) void qc.invalidateQueries({ queryKey: keys.element(item.id) });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.tables });
  invalidateResolved(qc);
  return null;
}

/** New schema… and Rename…: one name. */
export function SchemaNameDialog({
  title,
  initial = "",
  onClose,
  onSubmit,
}: {
  title: string;
  initial?: string;
  onClose: () => void;
  onSubmit: (name: string) => Promise<string | null>;
}) {
  const [name, setName] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    if (!name.trim() || busy) return;
    setBusy(true);
    const reason = await onSubmit(name.trim());
    setBusy(false);
    if (reason) setError(reason);
    else onClose();
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={title}>
        <form
          className="flex flex-col gap-2"
          data-testid="schema-name-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void submit();
          }}
        >
          <Field label="Name" htmlFor="schema-name" hint="Such as sales.">
            <Input id="schema-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!name.trim() || busy} data-testid="schema-name-save">
              Save
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** New schema… from the explorer: adds a schema to a database. */
export function NewSchemaDialog({ database, onClose }: { database: string; onClose: () => void }) {
  const run = useSchemaOperation();
  const { store } = useServices();
  return (
    <SchemaNameDialog
      title="New schema"
      onClose={onClose}
      onSubmit={async (name) => {
        const reason = await run({ op: "add-schema", id: database, name });
        if (!reason) store.getState().notify(`Created schema ${name}.`);
        return reason;
      }}
    />
  );
}

type Rec = Record<string, unknown>;

/** Remove schema: what lives there, the schema it moves to and, for the default, the new default. */
function RemoveSchemaDialog({ database, json, schema, onClose }: { database: string; json: Rec; schema: string; onClose: () => void }) {
  const run = useSchemaOperation();
  const tables = useDatabaseTables(database);
  const index = useIndex();
  const schemas = schemasOf(json);
  const current = schemas.find((s) => s.id === schema);
  const others = schemas.filter((s) => s.id !== schema);
  const isDefault = !!current && current.name === defaultSchemaName(json);
  const nameOf = (id: string) => index.data?.find((r) => r.id === id)?.name ?? id;
  const living = [
    ...(tables.data?.tables ?? []).filter((t) => t.schema === current?.name).map((t) => t.name),
    ...Object.entries(conventionSchemas(json))
      .filter(([, s]) => s === schema)
      .map(([p]) => `${nameOf(p)} (domain)`),
  ];
  const [target, setTarget] = useState(others[0]?.id ?? "");
  const [fallback, setFallback] = useState(others[0]?.id ?? "");
  const [error, setError] = useState<string | null>(null);
  const blocked = (living.length > 0 || isDefault) && !others.length;
  const remove = async () => {
    const reason = await run({
      op: "remove-schema",
      id: database,
      schema,
      ...(target && living.length ? { target } : {}),
      ...(isDefault && fallback ? { default: fallback } : {}),
    });
    if (reason) setError(reason);
    else onClose();
  };
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={`Remove schema ${current?.name ?? ""}?`}>
        <div className="flex flex-col gap-2" data-testid="remove-schema-dialog">
          <p className="max-h-40 overflow-auto text-12 text-secondary" data-testid="remove-schema-living">
            {living.length ? `Lives here: ${living.join(", ")}` : "Nothing lives here."}
          </p>
          {living.length && others.length ? (
            <Field label="Move to" htmlFor="remove-schema-target">
              <Select id="remove-schema-target" value={target} onChange={(e) => setTarget(e.target.value)}>
                {others.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                  </option>
                ))}
              </Select>
            </Field>
          ) : null}
          {isDefault && others.length ? (
            <Field label="New default schema" htmlFor="remove-schema-default">
              <Select id="remove-schema-default" value={fallback} onChange={(e) => setFallback(e.target.value)}>
                {others.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                  </option>
                ))}
              </Select>
            </Field>
          ) : null}
          {blocked ? <p className="text-12 text-danger">Add another schema first.</p> : null}
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="button" variant="danger" disabled={blocked} onClick={() => void remove()} data-testid="remove-schema-confirm">
              Remove
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

/** The database inspector's Schemas section. */
export function DatabaseSchemasSection({ id, json, flush }: { id: string; json: Rec; flush: () => void }) {
  const run = useSchemaOperation();
  const { store } = useServices();
  const schemas = schemasOf(json);
  const defaultName = defaultSchemaName(json);
  const [dialog, setDialog] = useState<{ type: "add" } | { type: "rename" | "remove"; schema: string } | null>(null);
  const act = async (op: SchemaOp) => {
    flush();
    const reason = await run(op);
    if (reason) store.getState().notify(reason, "error");
  };
  return (
    <section className="col-span-2 flex flex-col" data-testid="database-schemas">
      <SectionTitle>Schemas</SectionTitle>
      {schemas.length ? (
        <ul className="flex flex-col">
          {schemas.map((s) => {
            const isDefault = s.name === defaultName;
            return (
              <li key={s.id} className="flex h-6 items-center gap-2 text-12" data-testid={`schema-row-${s.name}`}>
                <span className="min-w-0 flex-1 truncate font-mono">{s.name}</span>
                {isDefault && schemas.length > 1 ? <span className="text-11 text-secondary">Default schema</span> : null}
                {!isDefault ? (
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => void act({ op: "set-default-schema", id, schema: s.id })}
                    data-testid={`schema-default-${s.name}`}
                  >
                    Set default
                  </Button>
                ) : null}
                <Button size="sm" variant="ghost" onClick={() => setDialog({ type: "rename", schema: s.id })} data-testid={`schema-rename-${s.name}`}>
                  Rename
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setDialog({ type: "remove", schema: s.id })} data-testid={`schema-remove-${s.name}`}>
                  Remove
                </Button>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="text-12 text-secondary">{defaultName ? `No schemas declared; tables go to ${defaultName}.` : "No schemas declared."}</p>
      )}
      <div>
        <Button size="sm" onClick={() => setDialog({ type: "add" })} data-testid="schema-add">
          Add schema
        </Button>
      </div>
      {dialog?.type === "add" ? (
        <SchemaNameDialog
          title="New schema"
          onClose={() => setDialog(null)}
          onSubmit={(name) => {
            flush();
            return run({ op: "add-schema", id, name });
          }}
        />
      ) : null}
      {dialog?.type === "rename" ? (
        <SchemaNameDialog
          title="Rename schema"
          initial={schemas.find((s) => s.id === dialog.schema)?.name ?? ""}
          onClose={() => setDialog(null)}
          onSubmit={(name) => {
            flush();
            return run({ op: "rename-schema", id, schema: dialog.schema, name });
          }}
        />
      ) : null}
      {dialog?.type === "remove" ? <RemoveSchemaDialog database={id} json={json} schema={dialog.schema} onClose={() => setDialog(null)} /> : null}
    </section>
  );
}
