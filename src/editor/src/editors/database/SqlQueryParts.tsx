// The tabs of a query written as SQL text (sql-queries-design.md section 13): SQL text (one editor per dialect, with each `:name`
// marked and a placeholder that names no parameter underlined, live from the editor's own scanner), Columns (what each row has) and
// Tables (what the SQL reads and writes, with Find in SQL). Parameters and the SQL preview are the built query's own tabs.
import { useMemo, useState, type ReactNode } from "react";
import { Plus, Search, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { useServices } from "@/app/context";
import { useDatabaseView, useElements, useIndex } from "@/api/queries";
import type { DatabaseView, Diagnostic } from "@/api/types";
import type { CodeHighlight, CodeMarker } from "@/code/CodeEditor";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/misc";
import { queryTextTemplate } from "@/explorer/databaseCreate";
import { addColumn, addToList, findTablesInSql, removeColumn, removeFromList, runningText, type TableChoice } from "@/model/sqlQuery";
import { scanSqlText, sqlDialectOf, sqlTextNamesNoParameter, sqlTextPosition } from "@/model/sqlText";
import { renameQueryColumn } from "@/workspaces/database/columnRename";
import type { EditorContext } from "../EditorFrame";
import { DialectBodies } from "./DialectBodies";
import { CommitInput } from "./fields";
import { TypeSelect, useDatabaseTypes } from "./TypedRowsGrid";

type Rec = Record<string, unknown>;

const head = "h-6 px-1 text-left text-11 font-semibold text-secondary";

/** The findings at a JSON pointer (or under it), each as one line: the rule and its message. */
export function Findings({ findings, at, testId }: { findings: readonly Diagnostic[]; at: string; testId?: string }) {
  const own = findings.filter((d) => d.jsonPointer === at || (d.jsonPointer ?? "").startsWith(`${at}/`));
  if (!own.length) return null;
  return (
    <ul role="alert" className="flex flex-col gap-0.5 text-12" data-testid={testId}>
      {own.map((d, i) => (
        <li key={i} className={d.severity === "error" ? "text-danger" : "text-secondary"}>
          <span className="font-mono">{d.rule}</span> {d.message}
        </li>
      ))}
    </ul>
  );
}

/**
 * The marks of one dialect's text: every `:name` in the placeholder style, and an underline (with the MQ4024 text as its hover) under
 * each that names no declared parameter. Read from the text as typed, with no call to the server.
 */
export function placeholderMarks(
  queryName: string,
  key: string,
  text: string,
  parameters: readonly string[],
  databaseDialect: string,
): { markers: CodeMarker[]; highlights: CodeHighlight[]; used: string[]; unknown: string[] } {
  const scan = scanSqlText(text, sqlDialectOf(key, databaseDialect));
  const declared = new Set(parameters);
  const markers: CodeMarker[] = [];
  const highlights: CodeHighlight[] = [];
  const used: string[] = [];
  const unknown: string[] = [];
  for (const p of scan.placeholders) {
    const { line, column } = sqlTextPosition(text, p.offset);
    highlights.push({ line, column, length: p.length });
    if (!used.includes(p.name)) used.push(p.name);
    if (declared.has(p.name)) continue;
    if (!unknown.includes(p.name)) unknown.push(p.name);
    markers.push({
      line,
      column,
      length: p.length,
      severity: "error",
      message: sqlTextNamesNoParameter(queryName, p.name, line, column, key),
    });
  }
  return { markers, highlights, used, unknown };
}

export function SqlTextTab({ ctx, dialect, findings }: { ctx: EditorContext; dialect: string; findings: readonly Diagnostic[] }) {
  // MQ4029 (no text for the dialect, at /sql) and MQ4098's verb cases (at /sql/<key>) are about the texts, so they show here too.
  const about = findings.filter((d) => (d.rule === "MQ4029" || d.rule === "MQ4098") && (d.jsonPointer === "/sql" || (d.jsonPointer ?? "").startsWith("/sql/")));
  const doc = ctx.json as unknown as Rec;
  const parameters = ((doc.parameters as { name: string }[] | undefined) ?? []).map((p) => p.name);
  const analyze = (key: string, text: string): { markers: CodeMarker[]; highlights: CodeHighlight[]; summary: ReactNode } => {
    const m = placeholderMarks(ctx.name, key, text, parameters, dialect);
    return {
      markers: m.markers,
      highlights: m.highlights,
      summary: (
        <p className="border-t border-default px-2 py-0.5 text-11 text-secondary" data-testid={`sqltext-placeholders-${key === "*" ? "any" : key}`}>
          {m.used.length ? (
            <>
              Parameters used: <span className="font-mono">{m.used.map((n) => `:${n}`).join(", ")}</span>
            </>
          ) : (
            "The text uses no parameter."
          )}
          {m.unknown.length ? (
            <span className="ml-3 text-danger" data-testid="sqltext-unknown-parameters">
              Names no parameter: <span className="font-mono">{m.unknown.map((n) => `:${n}`).join(", ")}</span>
            </span>
          ) : null}
        </p>
      ),
    };
  };
  return (
    <div className="flex flex-col gap-2">
      <Findings findings={about} at="/sql" testId="sqltext-problems" />
      <DialectBodies
        ctx={ctx}
        member="sql"
        prefix="sqltext"
        noun="Query"
        intro="The SQL the query runs, per dialect. Write each parameter as :name; a text for any dialect (*) serves the dialects without one of their own."
        template={queryTextTemplate}
        required
        analyze={analyze}
      />
    </div>
  );
}

export function SqlColumnsTab({ ctx, findings }: { ctx: EditorContext; findings: readonly Diagnostic[] }) {
  const services = useServices();
  const qc = useQueryClient();
  const doc = ctx.json as unknown as Rec;
  const columns = (Array.isArray(doc.columns) ? doc.columns : []) as Rec[];
  const types = useDatabaseTypes(String(doc.database ?? ""));
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  const at = (j: Rec, i: number) => (j.columns as Rec[])[i];
  return (
    <div className="flex flex-col gap-2" data-testid="query-columns">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          The columns each row has, by the name the SQL returns and with a type. Nothing checks them against a database. No columns: the statement returns no
          rows the code reads (a command).
        </p>
        <Button size="sm" variant="ghost" onClick={() => update(addColumn)} data-testid="query-add-column">
          <Plus /> Add column
        </Button>
      </div>
      {/* The text's own finding about the missing columns (a SELECT with none), shown where the fix is. */}
      <Findings
        findings={findings.filter((d) => d.rule === "MQ4098" && /declares no columns/.test(d.message))}
        at="/sql"
        testId="query-columns-text-problems"
      />
      {/* What a row shows under its name or type is not repeated here. */}
      <Findings
        findings={findings.filter((d) => !/^\/columns\/\d+\/(name|type)(\/|$)/.test(d.jsonPointer ?? ""))}
        at="/columns"
        testId="query-columns-problems"
      />
      {columns.length ? (
        <table className="w-full table-fixed border-collapse text-12" data-testid="query-columns-grid">
          <thead>
            <tr>
              <th scope="col" className={`${head} w-44`}>
                Name
              </th>
              <th scope="col" className={`${head} w-40`}>
                Type
              </th>
              <th scope="col" className={`${head} w-20`}>
                Can be null
              </th>
              <th scope="col" className={head}>
                Description
              </th>
              <th scope="col" className="w-7">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {columns.map((c, i) => (
              <tr key={i} className="border-t border-default align-top" data-testid={`query-column-${String(c.name ?? i)}`}>
                <td className="h-[var(--mq-row-h)] px-1 py-0.5">
                  <CommitInput
                    label={`Name of column ${i + 1}`}
                    mono
                    className="h-6 text-12"
                    value={String(c.name ?? "")}
                    onCommit={(v) => {
                      // The bindings that read the query by this column's name follow, in the same batch.
                      if (v.trim() && v.trim() !== String(c.name ?? "")) void renameQueryColumn(services, qc, ctx.id, i, v);
                    }}
                  />
                  <Findings findings={findings} at={`/columns/${i}/name`} />
                </td>
                <td className="px-1 py-0.5">
                  <TypeSelect
                    label={`Type of column ${String(c.name ?? i + 1)}`}
                    value={String(c.type ?? "")}
                    types={types}
                    allowNative={false}
                    onChange={(v) => v && update((j) => void (at(j, i).type = v))}
                  />
                  <Findings findings={findings} at={`/columns/${i}/type`} />
                </td>
                <td className="px-1 py-0.5">
                  <input
                    type="checkbox"
                    aria-label={`Column ${String(c.name ?? i + 1)} can be null`}
                    checked={c.nullable !== false}
                    onChange={(e) =>
                      update((j) => {
                        if (e.target.checked) delete at(j, i).nullable;
                        else at(j, i).nullable = false;
                      })
                    }
                  />
                </td>
                <td className="px-1 py-0.5">
                  <CommitInput
                    label={`Description of column ${String(c.name ?? i + 1)}`}
                    className="h-6 text-12"
                    value={String(c.description ?? "")}
                    onCommit={(v) =>
                      update((j) => {
                        if (v.trim()) at(j, i).description = v.trim();
                        else delete at(j, i).description;
                      })
                    }
                  />
                </td>
                <td className="px-1 py-0.5">
                  <Button size="icon-row" variant="ghost" label={`Delete column ${String(c.name ?? i + 1)}`} onClick={() => update((j) => removeColumn(j, i))}>
                    <Trash2 />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <EmptyState title="No columns">
          The statement returns no rows the code reads; it is generated as a command that returns the number of rows changed.
        </EmptyState>
      )}
    </div>
  );
}

/** The tables and views of a database a query may read: a table's key, a view's id. */
export function tableChoices(view: DatabaseView | null | undefined): TableChoice[] {
  if (!view) return [];
  const label = (a: TableChoice, b: TableChoice) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id);
  return [
    ...view.tables.map((t): TableChoice => ({ id: t.key, name: t.name, schema: t.schema, kind: "table" })).sort(label),
    ...view.views.map((v): TableChoice => ({ id: v.id, name: v.name, schema: v.schema, kind: "view" })).sort(label),
  ];
}

const choiceLabel = (c: TableChoice) => `${c.schema ? `${c.schema}.` : ""}${c.name}${c.kind === "view" ? " (view)" : ""}`;

export function SqlTablesTab({ ctx, dialect, findings }: { ctx: EditorContext; dialect: string; findings: readonly Diagnostic[] }) {
  const doc = ctx.json as unknown as Rec;
  const database = String(doc.database ?? "");
  const view = useDatabaseView(database || null).data?.view ?? null;
  const index = useIndex();
  const choices = useMemo(() => tableChoices(view), [view]);
  const reads = (Array.isArray(doc.reads) ? doc.reads : []) as string[];
  const writes = (Array.isArray(doc.writes) ? doc.writes : []) as string[];
  const texts = doc.sql as Record<string, string> | undefined;
  const [found, setFound] = useState<TableChoice[] | null>(null);
  const [ticked, setTicked] = useState<ReadonlySet<string>>(new Set());
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  // A reference as a name: a table key, a table file's id or an entity id for its table, a view, routine, sequence or type.
  const names = useMemo(() => {
    const map = new Map<string, string>();
    for (const c of choices) map.set(c.id, choiceLabel(c));
    for (const r of index.data ?? []) if (r.database === database && !map.has(r.id)) map.set(r.id, r.name);
    for (const t of view?.tables ?? []) {
      const file = t.key.split("@")[0];
      if (!map.has(file)) map.set(file, choiceLabel({ id: t.key, name: t.name, schema: t.schema, kind: "table" }));
    }
    return map;
  }, [choices, index.data, database, view]);
  const entityNames = useElements([...reads, ...writes].filter((x) => !names.has(x))).byId;
  const nameOf = (ref: string) => names.get(ref) ?? String(entityNames.get(ref)?.json.name ?? ref);
  const run = runningText(texts, dialect);
  const find = () => {
    setFound(findTablesInSql(texts, dialect, choices, reads));
    setTicked(new Set());
  };
  const section = (member: "reads" | "writes", title: string, hint: string, list: string[], options: TableChoice[]) => (
    <section className="flex flex-col gap-1" aria-label={title} data-testid={`query-${member}`}>
      <div className="flex items-center gap-2">
        <h3 className="text-12 font-semibold">{title}</h3>
        <span className="min-w-0 flex-1 text-11 text-secondary">{hint}</span>
        <select
          aria-label={`Add to ${title}`}
          className="h-6 w-56 rounded-[4px] border border-input bg-surface px-1 text-12"
          value=""
          data-testid={`query-${member}-add`}
          onChange={(e) => e.target.value && update((j) => addToList(j, member, [e.target.value]))}
        >
          <option value="">Add…</option>
          {options
            .filter((o) => !list.includes(o.id))
            .map((o) => (
              <option key={o.id} value={o.id}>
                {choiceLabel(o)}
              </option>
            ))}
        </select>
      </div>
      {list.length ? (
        <ul className="flex flex-col">
          {list.map((ref, i) => (
            <li key={`${ref}-${i}`} className="flex flex-col border-t border-default py-0.5" data-testid={`query-${member}-${nameOf(ref)}`}>
              <div className="flex items-center gap-2">
                <span className="min-w-0 flex-1 truncate font-mono text-12">{nameOf(ref)}</span>
                <Button
                  size="icon-row"
                  variant="ghost"
                  label={`Remove ${nameOf(ref)} from ${title}`}
                  onClick={() => update((j) => removeFromList(j, member, i))}
                >
                  <Trash2 />
                </Button>
              </div>
              <Findings findings={findings} at={`/${member}/${i}`} testId={`query-${member}-problem-${nameOf(ref)}`} />
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-12 text-secondary">None.</p>
      )}
    </section>
  );
  return (
    <div className="flex flex-col gap-3" data-testid="query-tables">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          What the SQL reads and writes, as you declare it: Maquettiste does not read the SQL for it. The lists show up as references, so changing or deleting a
          table lists this query.
        </p>
        <Button
          size="sm"
          variant="ghost"
          disabled={!run}
          onClick={find}
          title="List the tables and views whose names appear in the SQL text"
          data-testid="query-find-in-sql"
        >
          <Search /> Find in SQL
        </Button>
      </div>
      {/* The text's own finding about a missing Writes entry (an UPDATE with none), shown where the fix is. */}
      <Findings
        findings={findings.filter((d) => d.rule === "MQ4098" && /declares no table it writes/.test(d.message))}
        at="/sql"
        testId="query-tables-text-problems"
      />
      {found ? (
        <div className="flex flex-col gap-1 rounded-control border border-default p-1" data-testid="query-found-in-sql">
          {found.length ? (
            <>
              <p className="text-12 text-secondary">Names found in the SQL text. Tick the ones to add to Reads.</p>
              <ul className="flex flex-col">
                {found.map((c) => (
                  <li key={c.id}>
                    <label className="flex items-center gap-2 text-12">
                      <input
                        type="checkbox"
                        checked={ticked.has(c.id)}
                        onChange={(e) => {
                          const next = new Set(ticked);
                          if (e.target.checked) next.add(c.id);
                          else next.delete(c.id);
                          setTicked(next);
                        }}
                      />
                      <span className="font-mono">{choiceLabel(c)}</span>
                    </label>
                  </li>
                ))}
              </ul>
              <div className="flex gap-2">
                <Button
                  size="sm"
                  disabled={!ticked.size}
                  data-testid="query-found-add"
                  onClick={() => {
                    update((j) =>
                      addToList(
                        j,
                        "reads",
                        found.filter((c) => ticked.has(c.id)).map((c) => c.id),
                      ),
                    );
                    setFound(null);
                  }}
                >
                  Add to Reads
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setFound(null)}>
                  Close
                </Button>
              </div>
            </>
          ) : (
            <div className="flex items-center gap-2">
              <p className="flex-1 text-12 text-secondary">No table or view of this database that is not already in Reads is named in the SQL text.</p>
              <Button size="sm" variant="ghost" onClick={() => setFound(null)}>
                Close
              </Button>
            </div>
          )}
        </div>
      ) : null}
      {section("reads", "Reads", "Tables and views, and what else the SQL calls.", reads, choices)}
      {section(
        "writes",
        "Writes",
        "Tables the SQL inserts into, updates or deletes from.",
        writes,
        choices.filter((c) => c.kind === "table"),
      )}
    </div>
  );
}
