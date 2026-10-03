// A routine's or SQL object's `dependsOn`: the tables, views, sequences, routines, database types and SQL objects of the same
// database that must exist before it. Listed by name with a Remove each, and an Add picker over the others; each change is one
// save, so one undo step.
import { useMemo } from "react";
import { X } from "lucide-react";
import { useDatabaseView, useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/input";
import { KindIcon } from "@/app/icons";
import type { ElementKind } from "@/api/types";
import { KIND_LABELS } from "@/model/labels";
import type { EditorContext } from "../EditorFrame";
import { toggleDependency } from "./databaseDocs";

type Rec = Record<string, unknown>;

/** The kinds a dependency may be, in the picker's order. */
export const DEPENDENCY_KINDS: readonly ElementKind[] = ["table", "view", "sequence", "routine", "database-type", "sql-object"];

export interface DependencyOption {
  id: string;
  kind: ElementKind;
  name: string;
}

/**
 * The elements of a database an object may depend on (not itself), by kind then name. A table is named by its table name: a
 * file without a name of its own (one that adjusts a table the model lays out) takes the resolved table's name from
 * `tableNames` (by the laid-out key, `<owner>@<database>`), else its id; the database side never names an entity.
 */
export function dependencyOptions(
  rows: readonly { id: string; kind: string; name: string; database?: string | null; entity?: string | null; relation?: string | null }[],
  database: string,
  self: string,
  tableNames: ReadonlyMap<string, string> = new Map(),
): DependencyOption[] {
  const laidOutName = (r: (typeof rows)[number]) => {
    const owner = r.entity ?? r.relation;
    return owner ? tableNames.get(`${owner}@${database}`) : undefined;
  };
  return rows
    .filter((r) => r.database === database && r.id !== self && (DEPENDENCY_KINDS as readonly string[]).includes(r.kind))
    .map((r) => ({ id: r.id, kind: r.kind as ElementKind, name: r.name || laidOutName(r) || r.id }))
    .sort((a, b) => DEPENDENCY_KINDS.indexOf(a.kind) - DEPENDENCY_KINDS.indexOf(b.kind) || a.name.localeCompare(b.name));
}

export function DependsOnField({ ctx, id }: { ctx: EditorContext; id: string }) {
  const doc = ctx.json as unknown as Rec;
  const index = useIndex();
  const database = String(doc.database ?? "");
  const view = useDatabaseView(database || null);
  const tables = view.data?.view?.tables;
  const options = useMemo(
    () => dependencyOptions(index.data ?? [], database, ctx.id, new Map((tables ?? []).map((t) => [t.key, t.name]))),
    [index.data, database, ctx.id, tables],
  );
  const chosen = Array.isArray(doc.dependsOn) ? (doc.dependsOn as string[]) : [];
  const byId = new Map(options.map((o) => [o.id, o]));
  const update = (dependency: string, remove: boolean) => {
    ctx.edit((j) => void toggleDependency(j as unknown as Rec, dependency, remove));
    ctx.flush();
  };
  const free = options.filter((o) => !chosen.includes(o.id));
  return (
    <Field label="Depends on" htmlFor={`${id}-depends-on`} hint="What must exist first; the scripts create it before this one.">
      <div className="flex flex-col gap-1" data-testid="depends-on">
        {chosen.length ? (
          <ul className="flex flex-col rounded-control border border-default text-12">
            {chosen.map((d) => {
              const o = byId.get(d);
              return (
                <li
                  key={d}
                  className="flex h-[var(--mq-row-h)] items-center gap-2 border-b border-default px-2 last:border-b-0"
                  data-testid={`depends-on-${o?.name ?? d}`}
                >
                  {o ? <KindIcon kind={o.kind} className="size-3.5 shrink-0 text-secondary" /> : null}
                  <span className="min-w-0 flex-1 truncate font-mono">{o?.name ?? `${d} (not in this database)`}</span>
                  <span className="text-11 text-secondary">{o ? KIND_LABELS[o.kind].toLowerCase() : ""}</span>
                  <Button size="icon-row" variant="ghost" label={`Remove ${o?.name ?? d} from Depends on`} onClick={() => update(d, true)}>
                    <X />
                  </Button>
                </li>
              );
            })}
          </ul>
        ) : (
          <p className="text-12 text-secondary">Nothing named: the scripts create it in the usual order of kinds.</p>
        )}
        <select
          id={`${id}-depends-on`}
          className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
          value=""
          disabled={!free.length}
          onChange={(e) => e.target.value && update(e.target.value, false)}
        >
          <option value="">{free.length ? "Add a dependency…" : "Nothing else in this database"}</option>
          {DEPENDENCY_KINDS.filter((k) => free.some((o) => o.kind === k)).map((k) => (
            <optgroup key={k} label={KIND_LABELS[k]}>
              {free
                .filter((o) => o.kind === k)
                .map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name}
                  </option>
                ))}
            </optgroup>
          ))}
        </select>
      </div>
    </Field>
  );
}
