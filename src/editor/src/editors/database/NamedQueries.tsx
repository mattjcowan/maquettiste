// The Named queries (WITH) tab of a built query (sql-queries-design.md section 13): one card per named query, each a select with a
// name that the query's sources (and the named queries after it) read like a table. A card has its Name, its Sources, Select and
// Filter (the same sections as the query's own), and a Repeats switch: switched on, a Repeating part panel holds the part that
// reads the named query's own name again and again, with how its rows are added (Union). The findings of rule MQ4099 show on the
// card or panel they are about. Every gesture is one `update` (one save, one undo step).
import { useState } from "react";
import { ArrowDown, ArrowUp, CircleAlert, Plus, Trash2 } from "lucide-react";
import type { Diagnostic, QueryView } from "@/api/types";
import { Button } from "@/components/ui/button";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import {
  columnOptions,
  defaultAlias,
  fieldName,
  nameProblem,
  namedOptions,
  renameNamedField,
  renameNamedQuery,
  sourceOptions,
  type NamedQuery,
  type QueryDoc,
  type Subquery,
} from "@/model/queryTree";
import { CommitInput } from "./fields";
import { cellSelect } from "./queryExpression";
import { FilterSection, SelectSection, SourcesSection, type BodyUpdate, type QueryContext } from "./QueryParts";

/** The findings at a pointer prefix, as rows: the rule and its message. */
function Findings({ items, testid }: { items: readonly Diagnostic[]; testid: string }) {
  if (!items.length) return null;
  return (
    <ul role="alert" className="flex flex-col gap-0.5 text-12" data-testid={testid}>
      {items.map((d, i) => (
        <li key={i} className={`flex gap-1 ${d.severity === "error" ? "text-danger" : "text-secondary"}`}>
          <CircleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
          <span>
            <span className="font-mono">{d.rule}</span> {d.message}
          </span>
        </li>
      ))}
    </ul>
  );
}

export function NamedQueriesSection({
  doc,
  update,
  q,
  findings,
  resolved,
}: {
  doc: QueryDoc;
  update: (mutate: (d: QueryDoc) => void) => void;
  q: QueryContext;
  findings: readonly Diagnostic[];
  resolved?: QueryView["with"];
}) {
  const entries = doc.with ?? [];
  const add = () =>
    update((d) => {
      // Starts on the table the query itself reads, when it reads one: the usual named query is a step over the same table.
      const options = sourceOptions(q.view);
      const pick = options.find((o) => o.value === d.from?.source) ?? options[0];
      const alias = defaultAlias(pick?.name ?? "t");
      const column = columnOptions(pick ?? null)[0];
      const name = fieldName(
        "named",
        (d.with ?? []).map((e) => e.name),
      );
      d.with = [
        ...(d.with ?? []),
        {
          name,
          from: { source: pick?.value ?? "", alias },
          select: column ? [{ name: fieldName(column.name), expression: { column: `${alias}.${column.ref}` } }] : [],
        },
      ];
    });
  return (
    <div className="flex flex-col gap-2" data-testid="query-named">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          A named query is a select with a name. The query&apos;s sources, and the named queries after it, read it like a table. Switch on Repeats for one that
          reads its own name again and again (a chain of parents, a tree).
        </p>
        <Button size="sm" variant="ghost" onClick={add} data-testid="query-add-named">
          <Plus /> Add named query
        </Button>
      </div>
      {entries.length ? null : <EmptyState title="No named queries">The query reads tables and views only.</EmptyState>}
      {entries.map((entry, i) => (
        <NamedQueryCard
          key={i}
          index={i}
          entry={entry}
          q={q}
          entries={entries}
          resolved={resolved}
          findings={findings}
          update={update}
          move={(by) =>
            update((d) => {
              const list = [...(d.with ?? [])];
              const to = i + by;
              if (to < 0 || to >= list.length) return;
              [list[i], list[to]] = [list[to], list[i]];
              d.with = list;
            })
          }
          remove={() =>
            update((d) => {
              d.with = (d.with ?? []).filter((_, j) => j !== i);
              if (!d.with.length) delete d.with;
            })
          }
        />
      ))}
    </div>
  );
}

function NamedQueryCard({
  index,
  entry,
  entries,
  q,
  resolved,
  findings,
  update,
  move,
  remove,
}: {
  index: number;
  entry: NamedQuery;
  entries: readonly NamedQuery[];
  q: QueryContext;
  resolved?: QueryView["with"];
  findings: readonly Diagnostic[];
  update: (mutate: (d: QueryDoc) => void) => void;
  move: (by: -1 | 1) => void;
  remove: () => void;
}) {
  const [problem, setProblem] = useState<string | null>(null);
  const label = `Named query ${index + 1}`;
  const at = `/with/${index}`;
  const repeating = `${at}/recursive`;
  const own = (d: Diagnostic) => d.jsonPointer === at || (d.jsonPointer ?? "").startsWith(`${at}/`);
  const cardFindings = findings.filter((d) => own(d) && d.jsonPointer !== repeating && !(d.jsonPointer ?? "").startsWith(`${repeating}/`));
  const partFindings = findings.filter((d) => d.jsonPointer === repeating || (d.jsonPointer ?? "").startsWith(`${repeating}/`));
  // The anchor reads the named queries before it; the repeating part those before it and the named query itself.
  const anchorContext: QueryContext = { ...q, outer: [], named: namedOptions(entries, index, resolved) };
  const partContext: QueryContext = { ...q, outer: [], named: namedOptions(entries, index + 1, resolved) };
  const anchorUpdate: BodyUpdate = (mutate) => update((d) => mutate(d.with![index]));
  const partUpdate: BodyUpdate = (mutate) => update((d) => mutate(d.with![index].recursive as unknown as Subquery));
  const columns = resolved?.find((r) => r.name === entry.name)?.columns;
  const rename = (to: string) => {
    const next = to.trim();
    if (next === entry.name) return;
    const why = nameProblem(
      next,
      entries.filter((_, j) => j !== index).map((e) => e.name),
      "named query",
    );
    setProblem(why);
    if (!why) update((d) => void renameNamedQuery(d, entry.name, next));
  };
  const turnOn = () =>
    update((d) => {
      const e = d.with![index];
      const alias = defaultAlias(e.name);
      // Each field reads the same-named column of the named query itself, so the part starts out with the anchor's field names.
      e.recursive = {
        from: { source: e.name, alias },
        select: e.select.flatMap((f) => (f.name ? [{ name: f.name, expression: { column: `${alias}.${f.name}` } }] : [])),
      };
    });
  const part = entry.recursive;
  return (
    <section
      className="flex flex-col gap-2 rounded-control border border-default p-2"
      aria-label={`${label}: ${entry.name}`}
      data-testid={`query-named-${index + 1}`}
    >
      <div className="flex flex-wrap items-center gap-2">
        <label className="flex items-center gap-1 text-12 text-secondary">
          Name
          <CommitInput label={`${label} name`} mono className="h-6 w-40 text-12" value={entry.name} onCommit={rename} />
        </label>
        <label className="flex items-center gap-1 text-12">
          <input
            type="checkbox"
            aria-label={`${label} repeats`}
            checked={!!part}
            onChange={(e) =>
              e.target.checked
                ? turnOn()
                : update((d) => {
                    delete d.with![index].recursive;
                  })
            }
          />
          Repeats
        </label>
        {columns?.length ? (
          <span className="text-11 text-secondary" data-testid={`query-named-${index + 1}-columns`}>
            Columns: {columns.map((c) => `${c.name}${c.type ? ` (${c.type})` : ""}`).join(", ")}
          </span>
        ) : null}
        <span className="flex-1" />
        <Button size="icon-row" variant="ghost" label={`Move named query ${entry.name} up`} disabled={index === 0} onClick={() => move(-1)}>
          <ArrowUp />
        </Button>
        <Button size="icon-row" variant="ghost" label={`Move named query ${entry.name} down`} disabled={index === entries.length - 1} onClick={() => move(1)}>
          <ArrowDown />
        </Button>
        <Button size="icon-row" variant="ghost" label={`Remove named query ${entry.name}`} onClick={remove}>
          <Trash2 />
        </Button>
      </div>
      {problem ? (
        <p role="alert" className="text-12 text-danger">
          {problem}
        </p>
      ) : null}
      <Findings items={cardFindings} testid={`query-named-${index + 1}-findings`} />
      <SectionTitle>Sources</SectionTitle>
      <SourcesSection body={entry} update={anchorUpdate} q={anchorContext} label={label} />
      <SectionTitle>Select</SectionTitle>
      <SelectSection
        body={entry}
        update={anchorUpdate}
        q={anchorContext}
        entity={null}
        label={label}
        renameField={(i, to) => update((d) => void renameNamedField(d, entry.name, i, to))}
      />
      <SectionTitle>Filter</SectionTitle>
      <FilterSection body={entry} update={anchorUpdate} q={anchorContext} label={`${label} where`} />
      {part ? (
        <section
          className="ml-2 flex flex-col gap-2 rounded-control border border-default p-2"
          aria-label={`${label}: Repeating part`}
          data-testid={`query-named-${index + 1}-repeating`}
        >
          <div className="flex flex-wrap items-center gap-2">
            <h4 className="text-13 font-semibold">Repeating part</h4>
            <label className="flex items-center gap-1 text-12 text-secondary">
              Union
              <select
                aria-label={`${label} union`}
                className={`${cellSelect} font-sans`}
                value={part.union ?? "all"}
                onChange={(e) =>
                  update((d) => {
                    const target = d.with![index].recursive!;
                    if (e.target.value === "distinct") target.union = "distinct";
                    else delete target.union;
                  })
                }
              >
                <option value="all">All rows</option>
                <option value="distinct">Distinct rows</option>
              </select>
            </label>
            <span className="text-12 text-secondary">Reads {entry.name} once, and adds rows until it adds none.</span>
          </div>
          <Findings items={partFindings} testid={`query-named-${index + 1}-repeating-findings`} />
          <SectionTitle>Sources</SectionTitle>
          <SourcesSection body={part as unknown as Subquery} update={partUpdate} q={partContext} label={`${label} repeating`} />
          <SectionTitle>Select</SectionTitle>
          <SelectSection body={part as unknown as Subquery} update={partUpdate} q={partContext} entity={null} label={`${label} repeating`} />
          <SectionTitle>Filter</SectionTitle>
          <FilterSection body={part as unknown as Subquery} update={partUpdate} q={partContext} label={`${label} repeating where`} />
        </section>
      ) : null}
    </section>
  );
}
