// Deleting a part of a table (a column, the primary key, a unique constraint, an index, a foreign key, a check) from wherever it
// is asked (the Columns tab, a part tab's row, the inspector, the explorer, the diagram): its plan first (partDelete.ts), which
// says what else goes with it (whole keys and indexes holding a column, other tables' foreign keys to a key that goes, how many
// storage bindings follow) and what keeps it from being saved (a query that reads the column); then everything in one batch
// and one undo step, sent with the hashes the plan was read with (a document changed since is a conflict, never overwritten).
// A delete that takes nothing else goes at once; one that does waits for Delete in the dialog.
import { useEffect, useRef, useState } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { databaseViewQuery, elementQuery, indexQuery, useIndex } from "@/api/queries";
import type { TableView } from "@/api/types";
import { useServices, type AppServices as Services } from "@/app/context";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/misc";
import { useEditor } from "@/state/store";
import { planPartDelete, planReachesFurther, type PartDeletePlan } from "./partDelete";
import { bindingsFollow } from "./renames";
import { incomingForeignKeys, type TablePart } from "./tableParts";
import { commitDocuments, readDocuments } from "./tableBatch";

type Json = Record<string, unknown>;

export interface Request {
  database: string;
  key: string;
  part: TablePart;
}

export interface Prepared {
  plan: PartDeletePlan;
  /** The table as it was resolved when the delete was asked (its name). */
  table: TableView;
  label: string;
  /** The table the plan was made on. */
  key: string;
  /** Each document's hash as the plan read it: Delete sends these (a document changed since is a conflict). */
  hashes: Map<string, string>;
  /** Each document's file path (the storage bindings the dialog lists). */
  paths: Map<string, string>;
}

const READS = new Set(["view", "query", "mapping", "routine", "sql-object"]);

/**
 * The documents of the database a delete plan looks through: tables with keys to this one, its binders, mappings, queries, views, routines and SQL objects. `read` gets the hashes and paths.
 */
async function planDocuments(
  qc: QueryClient,
  database: string,
  key: string,
  tables: readonly TableView[],
  read: Map<string, { hash: string; path: string }>,
): Promise<Json[]> {
  const rows = await qc.fetchQuery(indexQuery);
  const kindOf = new Map(rows.map((r) => [r.id, r.kind]));
  const incoming = incomingForeignKeys(tables, key).filter((r) => r.table.key !== key);
  const ids = incoming.map((r) => r.table.key);
  for (const r of rows) if (r.database === database && r.id !== key && READS.has(r.kind)) ids.push(r.id);
  const refs = await endpoints.getReferences(key).catch(() => []);
  for (const r of refs) if (kindOf.get(r.fromElementId) === "entity" || kindOf.get(r.fromElementId) === "table") ids.push(r.fromElementId);
  return readDocuments(
    ids.filter((id) => id !== key),
    read,
  );
}

/** The plan of deleting `part` of table `key` as the documents are now. */
async function planNow(qc: QueryClient, database: string, key: string, part: TablePart, resolved: TableView | null) {
  const view = await qc.fetchQuery({ ...databaseViewQuery(qc, database), staleTime: 0 });
  const tables = view.view?.tables ?? [];
  const table = tables.find((t) => t.key === key) ?? resolved;
  const read = new Map<string, { hash: string; path: string }>();
  const file = await qc.fetchQuery({ ...elementQuery(key), staleTime: 0 });
  const doc = file.json as unknown as Json;
  read.set(key, { hash: file.hash, path: file.path });
  const docs = await planDocuments(qc, database, key, tables, read);
  const plan = planPartDelete({ table: doc, part, view: table, docs, dialect: view.view?.dialect ?? "postgresql" });
  return {
    plan,
    doc,
    hashes: new Map([...read].map(([id, r]) => [id, r.hash])),
    paths: new Map([...read].map(([id, r]) => [id, r.path])),
  };
}

/** The plan of a delete, read now. Resolves to the plan, or why there is none (said by the caller). */
export async function preparePartDelete(_services: Pick<Services, "store" | "drafts">, qc: QueryClient, request: Request): Promise<Prepared | string> {
  const { database, part, key } = request;
  const view = await qc.fetchQuery({ ...databaseViewQuery(qc, database), staleTime: 0 });
  const table = (view.view?.tables ?? []).find((t) => t.key === key);
  if (!table) return "The table is no longer in the database.";
  const first = await planNow(qc, database, key, part, table);
  if (!first) return "The table is no longer in the database.";
  return {
    plan: first.plan,
    table,
    label: `Delete ${first.plan.subject} of ${table.name}`,
    key,
    hashes: first.hashes,
    paths: first.paths,
  };
}

/**
 * Writes a prepared delete in one batch, with the hashes its plan was read with. Resolves to null when done, else why not (said).
 */
export async function executePartDelete(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  _request: Request,
  prepared: Prepared,
): Promise<string | null> {
  const fail = (why: string) => {
    services.store.getState().notify(`${prepared.label}: ${why}`, "error");
    return why;
  };
  if (prepared.plan.blockers.length) return fail(prepared.plan.blockers[0]);
  return commitDocuments(services, qc, prepared.label, prepared.plan.edits, { expected: prepared.hashes });
}

/** After a delete: the inspector leaves the part it showed. */
function forget(services: Pick<Services, "store">, request: Request): void {
  const s = services.store.getState();
  const shown = s.inspectedTable;
  if (!shown || shown.database !== request.database) return;
  const sameTable = shown.key === request.key;
  if (!sameTable) return;
  const { part } = request;
  if ((part.kind === "column" && shown.column) || (shown.part?.kind === part.kind && (part.kind === "primary-key" || shown.part.id === part.id)))
    s.inspectPart(null);
}

/** The plan dialog, mounted once by the app shell. */
export function PartDeleteHost() {
  const services = useServices();
  const qc = useQueryClient();
  const request = useEditor(services.store, (s) => s.partDelete);
  const [prepared, setPrepared] = useState<Prepared | null>(null);
  const [busy, setBusy] = useState(false);
  const shown = useRef<{ request: Request; prepared: Prepared } | null>(null);
  const close = () => {
    shown.current = null;
    setPrepared(null);
    services.store.getState().requestPartDelete(null);
  };

  useEffect(() => {
    if (!request) return;
    let live = true;
    setPrepared(null);
    const req = { database: request.database, key: request.key, part: request.part };
    void (async () => {
      try {
        await services.drafts.flushAll();
        const ready = await preparePartDelete(services, qc, req);
        if (!live) {
          return;
        }
        if (typeof ready === "string") {
          services.store.getState().notify(ready, "error");
          services.store.getState().requestPartDelete(null);
          return;
        }
        if (planReachesFurther(ready.plan)) {
          shown.current = { request: req, prepared: ready };
          setPrepared(ready);
          return;
        }
        // Nothing else goes: deleted at once.
        services.store.getState().requestPartDelete(null);
        const why = await executePartDelete(services, qc, req, ready);
        if (!why) {
          forget(services, req);
          services.store.getState().notify(`Deleted ${ready.plan.subject} of ${ready.table.name}.`);
        }
      } catch (e) {
        if (live) {
          services.store.getState().notify(`The delete failed: ${(e as Error).message}`, "error");
          services.store.getState().requestPartDelete(null);
        }
      }
    })();
    return () => {
      live = false;
    };
    // A new request (its nonce) plans again.
  }, [request?.nonce]); // eslint-disable-line react-hooks/exhaustive-deps

  const index = useIndex().data;
  if (!request || !prepared) return null;
  const { plan, table } = prepared;
  const req = { database: request.database, key: request.key, part: request.part };
  const run = async () => {
    setBusy(true);
    try {
      const why = await executePartDelete(services, qc, req, prepared);
      if (!why) {
        shown.current = null;
        forget(services, req);
        const n = plan.table.length + plan.others.reduce((sum, o) => sum + o.lines.length, 0) + plan.storage.length;
        services.store.getState().notify(`Deleted ${plan.subject} of ${table.name}${n ? ` and ${n} more ${n === 1 ? "thing" : "things"} with it` : ""}.`);
        close();
      }
    } finally {
      setBusy(false);
    }
  };
  const pathOf = (id: string) => prepared.paths.get(id) ?? index?.find((r) => r.id === id)?.path ?? id;
  return (
    <Dialog open onOpenChange={(open) => !open && close()}>
      <DialogContent title={`Delete ${plan.subject} of ${table.name}?`} className="w-[min(92vw,560px)]">
        <div className="flex flex-col gap-2 overflow-auto" data-testid="part-delete-plan">
          {plan.blockers.length ? (
            <ul role="alert" className="flex flex-col gap-0.5 text-danger" data-testid="part-delete-blockers">
              {plan.blockers.map((b) => (
                <li key={b}>{b}</li>
              ))}
            </ul>
          ) : null}
          {plan.table.length ? (
            <section aria-label={`What else goes from ${table.name}`}>
              <p className="font-medium">Also goes from {table.name}:</p>
              <ul className="list-disc pl-4" data-testid="part-delete-table">
                {plan.table.map((line) => (
                  <li key={line}>{line}</li>
                ))}
              </ul>
            </section>
          ) : null}
          {plan.others.length ? (
            <section aria-label="Other changes">
              <p className="font-medium">Changes elsewhere, so nothing points at what goes:</p>
              <ul className="flex flex-col gap-1" data-testid="part-delete-others">
                {plan.others.map((o) => (
                  <li key={o.id}>
                    <span className="font-mono">
                      {o.kind} {o.name}
                    </span>
                    <ul className="list-disc pl-4">
                      {o.lines.map((line) => (
                        <li key={line}>{line}</li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
            </section>
          ) : null}
          {plan.storage.length ? (
            <section aria-label="Storage bindings" data-testid="part-delete-storage">
              <p className="font-medium">{bindingsFollow(plan.storage.length)}.</p>
              <details>
                <summary className="cursor-pointer text-secondary">Files</summary>
                <ul className="flex flex-col gap-1" data-testid="part-delete-storage-files">
                  {plan.storage.map((o) => (
                    <li key={o.id}>
                      <span className="font-mono">{pathOf(o.id)}</span>
                      <ul className="list-disc pl-4">
                        {o.lines.map((line) => (
                          <li key={line}>{line}</li>
                        ))}
                      </ul>
                    </li>
                  ))}
                </ul>
              </details>
            </section>
          ) : null}
          {plan.warnings.length ? (
            <section aria-label="To check">
              <p className="font-medium">To check afterwards:</p>
              <ul className="list-disc pl-4 text-warning" data-testid="part-delete-warnings">
                {plan.warnings.map((w) => (
                  <li key={w}>{w}</li>
                ))}
              </ul>
            </section>
          ) : null}
          <p className="text-secondary" data-testid="part-delete-note">
            Everything listed changes together; undo puts it all back.
          </p>
        </div>
        <div className="flex items-center justify-end gap-2">
          {busy ? <Spinner label="Deleting" /> : null}
          <Button variant="ghost" onClick={close}>
            Cancel
          </Button>
          <Button variant="danger" disabled={busy || plan.blockers.length > 0} onClick={() => void run()} data-testid="part-delete-confirm">
            Delete
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
