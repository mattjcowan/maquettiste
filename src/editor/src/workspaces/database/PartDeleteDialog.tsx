// Deleting a part of a table (a column, the primary key, a unique constraint, an index, a foreign key, a check) from wherever it
// is asked (the Columns tab, a part tab's row, the inspector, the explorer, the diagram): its plan first (partDelete.ts), which
// says what else goes with it (whole keys and indexes holding a column, other tables' foreign keys to a key that goes, how many
// storage bindings follow) and what keeps it from being saved (a query that reads the column); then everything in one batch
// and one undo step, sent with the hashes the plan was read with (a document changed since is a conflict, never overwritten).
// A delete that takes nothing else goes at once; one that does waits for Delete in the dialog. A table not stored as a file yet
// (the one the part is on, or one whose foreign key goes with it) is stored first, under the delete's label, and the plan made
// on the stored files, so what the dialog shows is what Delete writes; a delete then cancelled or blocked takes the store back.
import { useEffect, useRef, useState } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { databaseViewQuery, elementQuery, indexQuery, keys, useIndex } from "@/api/queries";
import type { ElementSummary, TableView } from "@/api/types";
import { useServices, type AppServices as Services } from "@/app/context";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/misc";
import { useEditor } from "@/state/store";
import { planPartDelete, planReachesFurther, type PartDeletePlan } from "./partDelete";
import { bindingsFollow } from "./renames";
import { storableTables, columnIdMap, incomingForeignKeys, isLaidOutKey, resolvedTableDoc, type TablePart } from "./tableParts";
import { forgetStored, liveStoredFile, storeTables, storedFileOf } from "./storeTables";
import { commitDocuments, readDocuments, retractStep } from "./tableBatch";

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
  /** The table the plan was made on: its file when it was stored first. */
  key: string;
  /** Each document's hash as the plan read it: Delete sends these (a document changed since is a conflict). */
  hashes: Map<string, string>;
  /** Each document's file path (the storage bindings the dialog lists). */
  paths: Map<string, string>;
  /** The tables stored first for this delete (old key → file): taken back when the delete is cancelled or blocked. */
  stored: Map<string, string>;
}

const READS = new Set(["view", "query", "mapping", "routine", "sql-object"]);

/**
 * The documents of the database a delete plan looks through: tables with keys to this one (a table not stored as a file yet as
 * its resolved document), its binders, mappings, queries, views, routines and SQL objects. `read` gets the hashes and paths.
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
  const ids = incoming.map((r) => r.table.key).filter((k) => !isLaidOutKey(k));
  const laidOut = [...new Map(incoming.filter((r) => isLaidOutKey(r.table.key)).map((r) => [r.table.key, r.table])).values()];
  for (const r of rows) if (r.database === database && r.id !== key && READS.has(r.kind)) ids.push(r.id);
  if (!isLaidOutKey(key)) {
    const refs = await endpoints.getReferences(key).catch(() => []);
    for (const r of refs) if (kindOf.get(r.fromElementId) === "entity" || kindOf.get(r.fromElementId) === "table") ids.push(r.fromElementId);
  }
  const docs = await readDocuments(
    ids.filter((id) => id !== key),
    read,
  );
  return [...docs, ...laidOut.map((t) => resolvedTableDoc(t, database))];
}

/** The plan of deleting `part` of table `key` as the documents are now. */
async function planNow(qc: QueryClient, database: string, key: string, part: TablePart, resolved: TableView | null) {
  const view = await qc.fetchQuery({ ...databaseViewQuery(qc, database), staleTime: 0 });
  const tables = view.view?.tables ?? [];
  const table = tables.find((t) => t.key === key) ?? resolved;
  const read = new Map<string, { hash: string; path: string }>();
  let doc: Json;
  if (isLaidOutKey(key)) {
    if (!table) return null;
    doc = resolvedTableDoc(table, database);
  } else {
    const file = await qc.fetchQuery({ ...elementQuery(key), staleTime: 0 });
    doc = file.json as unknown as Json;
    read.set(key, { hash: file.hash, path: file.path });
  }
  const docs = await planDocuments(qc, database, key, tables, read);
  const plan = planPartDelete({ table: doc, part, view: table, docs, dialect: view.view?.dialect ?? "postgresql" });
  return {
    plan,
    doc,
    hashes: new Map([...read].map(([id, r]) => [id, r.hash])),
    paths: new Map([...read].map(([id, r]) => [id, r.path])),
  };
}

/**
 * The plan of a delete, read now. A table not stored as a file yet that the delete changes (its own, or one whose foreign key
 * goes) is stored first, under the delete's label, and the plan made again on the files; a plan that is then blocked takes the
 * store back. Resolves to the plan, or why there is none (said by the caller).
 */
export async function preparePartDelete(services: Pick<Services, "store" | "drafts">, qc: QueryClient, request: Request): Promise<Prepared | string> {
  const { database, part } = request;
  const view = await qc.fetchQuery({ ...databaseViewQuery(qc, database), staleTime: 0 });
  const tables = view.view?.tables ?? [];
  // A table stored a moment ago whose screen has not caught up: its file.
  const key = isLaidOutKey(request.key) ? ((await liveStoredFile(qc, request.key)) ?? request.key) : request.key;
  const table = tables.find((t) => t.key === request.key) ?? tables.find((t) => t.key === key);
  if (!table) return "The table is no longer in the database.";
  const first = await planNow(qc, database, key, part, table);
  if (!first) return "The table is no longer in the database.";
  const label = `Delete ${first.plan.subject} of ${table.name}`;
  const done = (plan: PartDeletePlan, on: string, read: { hashes: Map<string, string>; paths: Map<string, string> }, stored = new Map<string, string>()) => ({
    plan,
    table,
    label,
    key: on,
    hashes: read.hashes,
    paths: read.paths,
    stored,
  });
  const laidOut = [...new Set([key, ...first.plan.others.map((o) => o.id)].filter(isLaidOutKey))];
  if (!laidOut.length || first.plan.blockers.length) return done(first.plan, key, first);

  // Stored first: every table the delete changes that is not a file yet, in one store.
  const status = await endpoints.getMaterializeStatus(database);
  const owners = storableTables(
    tables.filter((t) => laidOut.includes(t.key)),
    database,
    status.entities,
    qc.getQueryData<ElementSummary[]>(keys.index),
  );
  const fixed = laidOut.filter((k) => !owners.has(k));
  if (fixed.length) {
    const names = fixed.map((k) => tables.find((t) => t.key === k)?.name ?? k);
    const blockers = [
      `${names.join(", ")} ${names.length === 1 ? "is" : "are"} not stored as a table file yet, and the model sets ${names.length === 1 ? "its" : "their"} keys: the delete cannot be made here yet.`,
    ];
    return done({ ...first.plan, blockers: [...first.plan.blockers, ...blockers] }, key, first);
  }
  const result = await storeTables(services, qc, database, owners, label);
  if (!result.ok) return "the table could not be stored as a file.";
  const file = result.files.get(key) ?? key;
  let on = part;
  if (part.kind === "column" && file !== key) {
    const doc = (await qc.fetchQuery({ ...elementQuery(file), staleTime: 0 })).json as unknown as Json;
    on = { ...part, id: columnIdMap(table.columns, doc).get(part.id) ?? part.id };
  }
  const again = await planNow(qc, database, file, on, null);
  const prepared = done(again?.plan ?? first.plan, file, again ?? first, result.files);
  if (!again || again.plan.blockers.length) await cancelPartDelete(services, qc, request, prepared);
  return prepared;
}

/** A delete not made after its tables were stored first: the store is taken back (no step of its own is left in the history). */
export async function cancelPartDelete(services: Pick<Services, "store" | "drafts">, qc: QueryClient, request: Request, prepared: Prepared): Promise<void> {
  if (!prepared.stored.size) return;
  const stored = prepared.stored;
  prepared.stored = new Map();
  if (await retractStep(services, qc, prepared.label)) forgetStored(services.store, request.database, stored);
}

/**
 * Writes a prepared delete in one batch, with the hashes its plan was read with, joining the store made for it. Resolves to
 * null when done, else why not (said); a delete that fails after its tables were stored first takes the store back.
 */
export async function executePartDelete(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  request: Request,
  prepared: Prepared,
): Promise<string | null> {
  const fail = (why: string) => {
    services.store.getState().notify(`${prepared.label}: ${why}`, "error");
    return why;
  };
  if (prepared.plan.blockers.length) return fail(prepared.plan.blockers[0]);
  if ([...prepared.plan.edits.keys()].some(isLaidOutKey)) return fail("a table it changes is not stored as a table file yet.");
  const why = await commitDocuments(services, qc, prepared.label, prepared.plan.edits, { join: prepared.stored.size > 0, expected: prepared.hashes });
  if (why) await cancelPartDelete(services, qc, request, prepared);
  else prepared.stored = new Map();
  return why;
}

/** After a delete: the inspector leaves the part it showed. */
function forget(services: Pick<Services, "store">, request: Request): void {
  const s = services.store.getState();
  const shown = s.inspectedTable;
  if (!shown || shown.database !== request.database) return;
  const sameTable = shown.key === request.key || shown.key === storedFileOf(request.key);
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
    // Cancelled: a store made for the plan is taken back.
    const open = shown.current;
    shown.current = null;
    if (open) void cancelPartDelete(services, qc, open.request, open.prepared);
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
          if (typeof ready !== "string") await cancelPartDelete(services, qc, req, ready);
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
  const storedNames = [...prepared.stored.keys()].map((k) =>
    k === request.key ? table.name : (index?.find((r) => r.id === prepared.stored.get(k))?.name ?? k),
  );
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
            {storedNames.length
              ? `${storedNames.join(", ")} ${storedNames.length === 1 ? "was" : "were"} stored as table files to make this plan; Cancel takes that back. `
              : ""}
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
