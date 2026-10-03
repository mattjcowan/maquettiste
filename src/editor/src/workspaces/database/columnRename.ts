// A table column's rename, from the column grid, the inspector, the explorer and the table editor: the column and what names it
// (renames.ts: the table's checks, index filters, computed expressions and defaults, the database's views and queries where the
// match is certain, the storage bindings that name it) in one batch and one undo step, after the table's earlier writes. A
// table not stored as a file yet is stored first (the same undo step). The notice says where the name was rewritten, how many
// storage bindings follow (never whose), and what to check by hand (routines and SQL objects that name it among them). A
// view's declared column renamed: its body's select list follows when CREATE VIEW names no column list, and so do the
// bindings that read it. A view's or query's column renamed anywhere else (its JSON): the same, as part of the same undo
// step. A table renamed anywhere: the notice lists the views, queries, routines and SQL objects that name the old name.
import { useEffect } from "react";
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { databaseViewQuery, elementQuery, indexQuery, keys } from "@/api/queries";
import type { ElementSummary } from "@/api/types";
import { useServices, type AppServices as Services } from "@/app/context";
import { bindingsFollow, planColumnRename, planTableRename, renameBindingColumns, renamedOutputColumns, renameViewOutput } from "./renames";
import { enqueueTableWrite, liveStoredFile, storeTables } from "./storeTables";
import { commitDocuments, readDocuments } from "./tableBatch";
import { columnIdMap, isLaidOutKey, storableTables } from "./tableParts";

type Json = Record<string, unknown>;

/** The database's documents whose SQL may name a table or a column: its views, queries, routines and SQL objects. */
async function sqlDocumentIds(qc: QueryClient, database: string): Promise<string[]> {
  const rows = await qc.fetchQuery(indexQuery);
  const kinds = new Set(["view", "query", "routine", "sql-object"]);
  return rows.filter((r) => kinds.has(r.kind) && r.database === database).map((r) => r.id);
}

/** A rename's notice: what was rewritten, how many storage bindings follow, and what to check by hand. */
export function renameNotice(done: string, rewritten: readonly string[], bindings: number, check: readonly string[]): string {
  const also = rewritten.length ? ` Also rewritten: ${rewritten.join("; ")}.` : "";
  const follow = bindings ? ` ${bindingsFollow(bindings)}.` : "";
  const checkText = check.length ? ` Check by hand (the name may still be used there): ${check.join("; ")}.` : "";
  return `${done}${also}${follow}${checkText}`;
}

/** The entities whose bindings name an element (a table, a view, a query), by the references to it. */
async function bindersOf(qc: QueryClient, id: string): Promise<string[]> {
  const rows = await qc.fetchQuery(indexQuery);
  const kinds = new Map(rows.map((r) => [r.id, r.kind]));
  const refs = await endpoints.getReferences(id).catch(() => []);
  return [...new Set(refs.map((r) => r.fromElementId).filter((x) => kinds.get(x) === "entity"))];
}

/**
 * Renames column `column` (its key in the resolved view) of table `key` to `to`. Resolves to whether it was saved, or null when
 * the table is neither a file nor storable as one (the caller writes the name where such a table keeps it).
 */
export function renameTableColumn(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  args: { database: string; key: string; column: string; to: string },
): Promise<boolean | null> {
  const { database, key, column } = args;
  const to = args.to.trim();
  return enqueueTableWrite(database, key, async () => {
    if (!to) return false;
    const view = await qc.fetchQuery(databaseViewQuery(qc, database));
    const tables = view.view?.tables ?? [];
    const table = tables.find((t) => t.key === key);
    let file = isLaidOutKey(key) ? await liveStoredFile(qc, key) : key;
    let join = false;
    if (!file) {
      if (!table) return false;
      const status = await endpoints.getMaterializeStatus(database);
      const owner = storableTables([table], database, status.entities, qc.getQueryData<ElementSummary[]>(keys.index)).get(key);
      if (!owner) return null;
      const stored = await storeTables(services, qc, database, new Map([[key, owner]]), `Rename column to ${to}`);
      file = stored.files.get(key);
      if (!file) return false;
      services.store.getState().notify(`${table.name} is now stored as a table file.`);
      join = true;
    }
    const doc = (await qc.fetchQuery({ ...elementQuery(file), staleTime: 0 })).json as unknown as Json;
    const columnId = isLaidOutKey(key) && table ? (columnIdMap(table.columns, doc).get(column) ?? column) : column;
    const from = String(((doc.columns as Json[] | undefined) ?? []).find((c) => c.id === columnId)?.name ?? "");
    if (!from || from === to) return !!from;
    const docs = await readDocuments([...(await sqlDocumentIds(qc, database)), ...(await bindersOf(qc, file))]);
    const plan = planColumnRename({ table: doc, column: columnId, to, docs, tables: view.view?.tables ?? [], dialect: view.view?.dialect });
    const label = `Rename column ${from} of ${String(doc.name ?? "")} to ${to}`;
    const why = await commitDocuments(services, qc, label, plan.edits, { join });
    if (why) return false;
    const s = services.store.getState();
    s.notify(renameNotice(`Renamed column ${from} to ${to}.`, plan.rewritten, plan.bindings.length, plan.check));
    if (plan.check.length) s.log("warning", `${label}: check ${plan.check.join("; ")}.`);
    return true;
  });
}

/**
 * Renames a view's declared column `index` to `to`, its body's select list when CREATE VIEW names no column list, and the
 * bindings that read the view by that name, in one batch and one undo step.
 */
export async function renameViewColumn(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  viewId: string,
  index: number,
  to: string,
): Promise<boolean> {
  const next = to.trim();
  const view = (await qc.fetchQuery({ ...elementQuery(viewId), staleTime: 0 })).json as unknown as Json;
  const from = String(((view.columns as Json[] | undefined) ?? [])[index]?.name ?? "");
  if (!next || !from || from === next) return false;
  const dialect = await databaseDialect(qc, String(view.database ?? ""));
  const renames = new Map([[from, next]]);
  const output = renameViewOutput(view, from, next, dialect);
  const edits = new Map<string, (doc: Json) => void>([
    [
      viewId,
      (d) => {
        const c = ((d.columns as Json[] | undefined) ?? [])[index];
        if (c) c.name = next;
        if (output.body) d.body = renameViewOutput(d, from, next, dialect).body ?? d.body;
      },
    ],
  ]);
  let bindings = 0;
  for (const entity of await readDocuments(await bindersOf(qc, viewId))) {
    if (!renameBindingColumns(JSON.parse(JSON.stringify(entity)) as Json, viewId, renames)) continue;
    bindings++;
    edits.set(String(entity.id), (d) => void renameBindingColumns(d, viewId, renames));
  }
  const why = await commitDocuments(services, qc, `Rename column ${from} of view ${String(view.name ?? "")} to ${next}`, edits);
  if (why) return false;
  const name = `view ${String(view.name ?? viewId)}`;
  if (output.body || bindings || output.check)
    services.store
      .getState()
      .notify(
        renameNotice(
          `Renamed column ${from} to ${next}.`,
          output.body ? [`the body of ${name}`] : [],
          bindings,
          output.check ? [`the body of ${name} (it does not name ${from} plainly)`] : [],
        ),
      );
  return true;
}

/** The dialect of a database (its resolved view's). */
async function databaseDialect(qc: QueryClient, database: string): Promise<string | undefined> {
  if (!database) return undefined;
  const view = await qc.fetchQuery(databaseViewQuery(qc, database)).catch(() => null);
  return view?.view?.dialect;
}

/** A table renamed (any screen): the views, queries, routines and SQL objects that still name the old name, said to check. */
async function sayTableRename(services: Pick<Services, "store">, qc: QueryClient, id: string, from: string, after: Json): Promise<void> {
  const database = String(after.database ?? "");
  if (!database) return;
  const docs = await readDocuments([...(await sqlDocumentIds(qc, database)), ...(await bindersOf(qc, id))]);
  const check = planTableRename({ table: after, from, docs, dialect: await databaseDialect(qc, database) });
  if (!check.length) return;
  const s = services.store.getState();
  s.notify(renameNotice(`Renamed table ${from} to ${String(after.name ?? "")}.`, [], 0, check));
  s.log("warning", `Rename table ${from}: check ${check.join("; ")}.`);
}

/**
 * Mounted once by the app shell: when a view's or a query's columns are renamed in place (its editor, its JSON tab), the view's
 * body (no column list) and the bindings that read it by those names follow; their saves join the rename's undo step. When a
 * table is renamed, what may still name the old name is said.
 */
export function useSourceRenameFollow(): void {
  const services = useServices();
  useEffect(
    () =>
      services.drafts.onSaved((id, before, after) => {
        const qc = services.queryClient;
        const was = before as unknown as Json | null;
        const now = after as unknown as Json;
        if (now.kind === "table" && was && typeof was.name === "string" && was.name !== now.name) {
          void sayTableRename(services, qc, id, was.name, now).catch(() => undefined);
          return;
        }
        const renames = renamedOutputColumns(was, now);
        if (!renames.size) return;
        void (async () => {
          let bindings = 0;
          const check: string[] = [];
          if (now.kind === "view") {
            const dialect = await databaseDialect(qc, String(now.database ?? ""));
            let body = now.body;
            for (const [from, to] of renames) {
              const r = renameViewOutput({ ...now, body }, from, to, dialect);
              if (r.body) body = r.body;
              else if (r.check) check.push(`the body of view ${String(now.name ?? id)} (it does not name ${from} plainly)`);
            }
            if (body !== now.body) {
              const doc = await qc.fetchQuery({ ...elementQuery(id), staleTime: 0 });
              services.drafts.edit(id, (json) => void ((json as unknown as Json).body = body), { base: doc, followUp: true });
              await services.drafts.flush(id);
            }
          }
          for (const entity of await bindersOf(qc, id)) {
            const doc = await qc.fetchQuery({ ...elementQuery(entity), staleTime: 0 });
            const probe = JSON.parse(JSON.stringify(doc.json)) as Json;
            if (!renameBindingColumns(probe, id, renames)) continue;
            bindings++;
            services.drafts.edit(entity, (json) => void renameBindingColumns(json as unknown as Json, id, renames), { base: doc, followUp: true });
            await services.drafts.flush(entity);
          }
          if (bindings || check.length)
            services.store.getState().notify(renameNotice(`Renamed ${[...renames].map(([a, b]) => `${a} to ${b}`).join(", ")}.`, [], bindings, check));
        })();
      }),
    [services],
  );
}
