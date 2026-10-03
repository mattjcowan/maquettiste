// The Databases explorer's rows under a table: what they show beyond the table's detail (its checks, the foreign keys pointing
// at it), what a click or an open does (the inspector shows the part alone; an open shows the table editor on the part's tab
// with the part picked), and their menu actions (New column, New unique constraint, New index, New foreign key, New check,
// Rename, Delete), each one change of the table, so one undo step; Rename on the table row itself. New foreign key opens the
// foreign key dialog; Delete shows the part's delete plan first (PartDeleteDialog).
import { useCallback } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { databaseViewQuery, elementQuery } from "@/api/queries";
import { useServices } from "@/app/context";
import { newId } from "@/lib/ids";
import { openTableEditor } from "@/workspaces/database/openTableEditor";
import { openNewForeignKeyFor } from "@/workspaces/database/foreignKeys";
import { renameTableColumn } from "@/workspaces/database/columnRename";
import { storedFileOf, updateTable } from "@/workspaces/database/storeTables";
import {
  addPart,
  incomingForeignKeys,
  isLaidOutKey,
  PART_LABELS,
  PART_TAB,
  renamePart,
  type TablePart,
  type TablePartKind,
} from "@/workspaces/database/tableParts";
import { databaseOf, nodeOf, tableOf, type Forest, type TableExtras, type TreeNode } from "./tree";

type Json = Record<string, unknown>;

/** A table row's checks (from its file) and the foreign keys of other tables pointing at it (from the database's view). */
export async function loadTableExtras(qc: QueryClient, database: string, node: TreeNode): Promise<TableExtras> {
  const key = node.table?.key ?? "";
  const extras: TableExtras = {};
  const [view, file] = await Promise.all([
    qc.fetchQuery(databaseViewQuery(qc, database)).catch(() => null),
    key && !isLaidOutKey(key) ? qc.fetchQuery(elementQuery(key)).catch(() => null) : Promise.resolve(null),
  ]);
  const tables = view?.view?.tables ?? [];
  extras.referencedBy = incomingForeignKeys(tables, key).map((r) => ({ tableKey: r.table.key, tableName: r.table.name, name: r.name, columns: r.columns }));
  const checks = Array.isArray((file?.json as Json | undefined)?.checks) ? ((file!.json as Json).checks as Json[]) : [];
  extras.checks = checks.map((c) => {
    const expression = (c.expression as Record<string, string> | undefined) ?? {};
    return { id: String(c.id ?? ""), name: String(c.name ?? c.id ?? ""), expression: Object.values(expression)[0] };
  });
  return extras;
}

/** The table a row under a table belongs to (its database and key), and the part it stands for. */
export function partOfRow(
  forest: Forest,
  key: string,
): { database: string; table: string; tableRow: string; part: TablePart | null; kind: TablePartKind; incoming: boolean } | null {
  const node = nodeOf(forest, key);
  if (!node?.part) return null;
  const tableRow = tableOf(forest, key);
  const owner = tableRow ? forest.nodes.get(tableRow) : undefined;
  const database = tableRow ? databaseOf(forest, tableRow) : undefined;
  if (!tableRow || !owner?.table || !database) return null;
  const incoming = !!node.partTable || node.key.endsWith("/referenced-by");
  return {
    database,
    table: node.partTable ?? owner.table.key,
    tableRow,
    part: node.type === "item" ? node.part : null,
    kind: node.part.kind,
    incoming,
  };
}

/** The explorer's handling of the rows under a table. */
export function useTableRowActions() {
  const services = useServices();
  const qc = useQueryClient();
  const { store } = services;

  /** A click (the inspector shows the part; an editor in front follows it) or an open (the table editor on the part's tab). */
  const openPart = useCallback(
    (forest: Forest, key: string, how: "click" | "open"): boolean => {
      const at = partOfRow(forest, key);
      if (!at) return false;
      const s = store.getState();
      const tab = PART_TAB[at.kind];
      if (!at.part) {
        // A folder: its tab, nothing picked.
        if (how === "open") openTableEditor(store, at.database, at.table, { tab, explorerItem: key });
        return true;
      }
      if (how === "open" || s.editors.active !== null) openTableEditor(store, at.database, at.table, { part: at.part, pin: how === "open", explorerItem: key });
      else {
        s.inspectTable(
          { database: at.database, key: at.table, column: at.part.kind === "column" ? at.part.id : null, part: at.part.kind === "column" ? null : at.part },
          key,
        );
        if (s.workspace === "database" && s.activeDatabase === at.database) s.setDatabaseTable(at.table);
      }
      return true;
    },
    [store],
  );

  /** New <part> on a table row or a part folder: added, then shown picked on its tab in the table editor. */
  const newPart = useCallback(
    async (forest: Forest, key: string, kind: TablePartKind) => {
      const node = nodeOf(forest, key);
      const tableRow = node?.type === "table" ? key : tableOf(forest, key);
      const owner = tableRow ? forest.nodes.get(tableRow) : undefined;
      const database = tableRow ? databaseOf(forest, tableRow) : undefined;
      if (!owner?.table || !database) return;
      const tableKey = owner.table.key;
      // A foreign key needs its referenced table and columns: the foreign key dialog asks for them.
      if (kind === "foreign-key") {
        await openNewForeignKeyFor(services, qc, database, tableKey);
        return;
      }
      const view = await qc.fetchQuery(databaseViewQuery(qc, database)).catch(() => null);
      let made: TablePart | null = null;
      const ok = await updateTable(services, qc, database, tableKey, `New ${PART_LABELS[kind]} on ${owner.table.name}`, (doc) => {
        made = addPart(doc, kind, newId, { dialect: view?.view?.dialect });
        return made ? undefined : false;
      });
      if (!made) {
        store.getState().notify(kind === "primary-key" ? "The table has a primary key already." : "Add a column to the table first.", "error");
        return;
      }
      if (!ok) return;
      // A table stored as a file just now opens as its file.
      openTableEditor(store, database, storedFileOf(tableKey) ?? tableKey, { part: made, pin: true });
    },
    [qc, services, store],
  );

  /** Delete on a part row: its plan first (what else goes with it), then one batch and one undo step (PartDeleteDialog). */
  const deletePart = useCallback(
    (forest: Forest, key: string) => {
      const at = partOfRow(forest, key);
      if (!at?.part || at.incoming) return;
      store.getState().requestPartDelete({ database: at.database, key: at.table, part: at.part });
    },
    [store],
  );

  const renameRow = useCallback(
    async (forest: Forest, key: string, name: string) => {
      const at = partOfRow(forest, key);
      if (!at?.part || at.incoming || !name.trim()) return;
      const view = await qc.fetchQuery(databaseViewQuery(qc, at.database)).catch(() => null);
      const table = view?.view?.tables.find((t) => t.key === at.table) ?? null;
      const part = at.part;
      // A column's rename rewrites what names it too (columnRename.ts).
      if (part.kind === "column" && (await renameTableColumn(services, qc, { database: at.database, key: at.table, column: part.id, to: name })) !== null)
        return;
      await updateTable(
        services,
        qc,
        at.database,
        at.table,
        `Rename ${PART_LABELS[part.kind]} on ${table?.name ?? "table"}`,
        (doc) => void renamePart(doc, part, name, table),
      );
    },
    [qc, services],
  );

  /** Rename on a table row: the table's name, one undo step (a table not stored as a file yet is stored first). */
  const renameTable = useCallback(
    async (forest: Forest, key: string, name: string) => {
      const node = nodeOf(forest, key);
      const database = databaseOf(forest, key);
      const next = name.trim();
      if (!node?.table || !database || !next) return;
      await updateTable(services, qc, database, node.table.key, `Rename table ${node.table.name} to ${next}`, (doc) => {
        doc.name = next;
      });
    },
    [qc, services],
  );

  return { openPart, newPart, deletePart, renameRow, renameTable };
}
