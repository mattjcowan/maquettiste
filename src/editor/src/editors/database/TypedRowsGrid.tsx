// A dense grid of typed rows: a routine's parameters, the columns of its table result, a composite database type's fields.
// Each row has a name, a type (a built-in type or a database type of the same database), length, precision, scale and a native
// type that replaces the type's; a parameter adds its mode and default, a result column whether it accepts nulls. Every commit
// is one edit and one save of the document, so one undo step.
import { useMemo, useState } from "react";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
import { useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/misc";
import { BUILTIN_TYPES } from "@/model/model";
import type { EditorContext } from "../EditorFrame";
import { CommitInput } from "./fields";
import { addTypedRow, facetProblem, moveRow, removeRow, setFacet, setRowNativeType, setRowType, type TypeFacet } from "./databaseDocs";

type Rec = Record<string, unknown>;

const cell = "h-[var(--mq-row-h)] px-1 align-middle";
const head = "h-6 px-1 text-left text-11 font-semibold text-secondary";
const select = "h-6 w-full rounded-[4px] border border-input bg-surface px-1 font-mono text-12";
const FACETS: TypeFacet[] = ["length", "precision", "scale"];
const FACET_LABELS: Record<TypeFacet, string> = { length: "Len", precision: "Prec", scale: "Scale" };

/** The database types of a database, by name, as the type pickers offer them (`except`: the type being edited). */
export function useDatabaseTypes(database: string, except?: string): { id: string; name: string }[] {
  const index = useIndex();
  return useMemo(
    () =>
      (index.data ?? [])
        .filter((r) => r.kind === "database-type" && r.database === database && r.id !== except)
        .map((r) => ({ id: r.id, name: r.name }))
        .sort((a, b) => a.name.localeCompare(b.name)),
    [index.data, database, except],
  );
}

/** A type picker: the built-in types, then the database's own types (by id). `allowNative`: a "(native type)" choice. */
export function TypeSelect({
  id,
  label,
  value,
  types,
  allowNative,
  onChange,
}: {
  id?: string;
  label?: string;
  value: string;
  types: readonly { id: string; name: string }[];
  allowNative: boolean;
  onChange: (value: string) => void;
}) {
  const known = !value || (BUILTIN_TYPES as readonly string[]).includes(value) || types.some((t) => t.id === value);
  return (
    <select id={id} aria-label={label} className={select} value={value} onChange={(e) => onChange(e.target.value)}>
      {allowNative || !value ? <option value="">(native type)</option> : null}
      <optgroup label="Built-in types">
        {BUILTIN_TYPES.map((t) => (
          <option key={t} value={t}>
            {t}
          </option>
        ))}
      </optgroup>
      {types.length ? (
        <optgroup label="Database types">
          {types.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </optgroup>
      ) : null}
      {known ? null : (
        <option value={value} disabled>
          {value} (not a type of this database)
        </option>
      )}
    </select>
  );
}

export interface TypedRowsGridProps {
  ctx: EditorContext;
  /** Where the rows live: ["parameters"], ["returns", "table"], ["fields"]. */
  path: readonly string[];
  /** The row's noun ("parameter", "column", "field") and the test id prefix. */
  noun: string;
  prefix: string;
  /** A parameter's mode and default. */
  parameter?: boolean;
  /** A result column's nullability. */
  nullable?: boolean;
  /** The list keeps at least one row (a table result). */
  keepOne?: boolean;
  /** The name a new row starts from (`<base>_<n>`) and its type. */
  base: string;
  newType?: string;
  intro: string;
  empty?: { title: string; text: string };
  /** The database type being edited, left out of its own type pickers. */
  self?: string;
}

/** The array at `path` of a document (created on demand for a write). */
function rowsAt(doc: Rec, path: readonly string[], create = false): Rec[] {
  let node: Rec = doc;
  for (const key of path.slice(0, -1)) {
    if (!node[key] || typeof node[key] !== "object") {
      if (!create) return [];
      node[key] = {};
    }
    node = node[key] as Rec;
  }
  const last = path[path.length - 1];
  return Array.isArray(node[last]) ? (node[last] as Rec[]) : [];
}

function parentAt(doc: Rec, path: readonly string[]): Rec {
  let node: Rec = doc;
  for (const key of path.slice(0, -1)) node = (node[key] ??= {}) as Rec;
  return node;
}

export function TypedRowsGrid({ ctx, path, noun, prefix, parameter, nullable, keepOne, base, newType, intro, empty, self }: TypedRowsGridProps) {
  const doc = ctx.json as unknown as Rec;
  const rows = rowsAt(doc, path);
  const types = useDatabaseTypes(String(doc.database ?? ""), self);
  const [problem, setProblem] = useState<string | null>(null);
  const member = path[path.length - 1];
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  /** Edits row `i`; `change` returns false to refuse (the message says why). */
  const editRow = (i: number, change: (row: Rec) => boolean, refused: string) => {
    const row = rows[i];
    if (!row || !change(structuredClone(row))) {
      setProblem(refused);
      return;
    }
    setProblem(null);
    update((j) => void change(rowsAt(j, path)[i]));
  };
  const name = (r: Rec, i: number) => String(r.name ?? i + 1);
  return (
    <div className="flex flex-col gap-2" data-testid={`${prefix}-grid`}>
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">{intro}</p>
        <Button size="sm" variant="ghost" onClick={() => update((j) => addTypedRow(parentAt(j, path), member, base, newType))} data-testid={`${prefix}-add`}>
          <Plus /> Add {noun}
        </Button>
      </div>
      {rows.length ? (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[48rem] table-fixed border-collapse text-12">
            <thead>
              <tr>
                <th scope="col" className={head}>
                  Name
                </th>
                <th scope="col" className={`${head} w-36`}>
                  Type
                </th>
                {FACETS.map((f) => (
                  <th key={f} scope="col" className={`${head} w-12`}>
                    {FACET_LABELS[f]}
                  </th>
                ))}
                <th scope="col" className={`${head} w-28`}>
                  Native type
                </th>
                {parameter ? (
                  <>
                    <th scope="col" className={`${head} w-20`}>
                      Mode
                    </th>
                    <th scope="col" className={`${head} w-24`}>
                      Default
                    </th>
                  </>
                ) : null}
                {nullable ? (
                  <th scope="col" className={`${head} w-10`}>
                    Null
                  </th>
                ) : null}
                <th scope="col" className="w-[4.5rem]">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i} className="border-t border-default" data-testid={`${prefix}-row-${name(r, i)}`}>
                  <td className={cell}>
                    <CommitInput
                      label={`Name of ${noun} ${i + 1}`}
                      mono
                      className="h-6 text-12"
                      value={String(r.name ?? "")}
                      onCommit={(v) =>
                        editRow(
                          i,
                          (row) => {
                            if (!v.trim()) return false;
                            row.name = v.trim();
                            return true;
                          },
                          `A ${noun} needs a name.`,
                        )
                      }
                    />
                  </td>
                  <td className={cell}>
                    <TypeSelect
                      label={`Type of ${noun} ${name(r, i)}`}
                      value={typeof r.type === "string" ? r.type : ""}
                      types={types}
                      allowNative={typeof r.nativeType === "string"}
                      onChange={(v) => editRow(i, (row) => setRowType(row, v), `A ${noun} needs a type or a native type.`)}
                    />
                  </td>
                  {FACETS.map((f) => (
                    <td key={f} className={cell}>
                      <CommitInput
                        label={`${f[0].toUpperCase()}${f.slice(1)} of ${noun} ${name(r, i)}`}
                        mono
                        className="h-6 text-12"
                        value={typeof r[f] === "number" ? String(r[f]) : ""}
                        onCommit={(v) => editRow(i, (row) => setFacet(row, f, v), `${f[0].toUpperCase()}${f.slice(1)}: ${facetProblem(f, v) ?? ""}`)}
                      />
                    </td>
                  ))}
                  <td className={cell}>
                    <CommitInput
                      label={`Native type of ${noun} ${name(r, i)}`}
                      mono
                      className="h-6 text-12"
                      value={String(r.nativeType ?? "")}
                      placeholder="from the type"
                      onCommit={(v) => editRow(i, (row) => setRowNativeType(row, v), `A ${noun} needs a type or a native type.`)}
                    />
                  </td>
                  {parameter ? (
                    <>
                      <td className={cell}>
                        <select
                          aria-label={`Mode of ${noun} ${name(r, i)}`}
                          className={select}
                          value={String(r.mode ?? "in")}
                          onChange={(e) =>
                            editRow(
                              i,
                              (row) => {
                                if (e.target.value === "in") delete row.mode;
                                else row.mode = e.target.value;
                                return true;
                              },
                              "",
                            )
                          }
                        >
                          <option value="in">in</option>
                          <option value="out">out</option>
                          <option value="inout">inout</option>
                        </select>
                      </td>
                      <td className={cell}>
                        <CommitInput
                          label={`Default of ${noun} ${name(r, i)}`}
                          mono
                          className="h-6 text-12"
                          value={String(r.default ?? "")}
                          placeholder="none"
                          onCommit={(v) =>
                            editRow(
                              i,
                              (row) => {
                                if (v.trim()) row.default = v.trim();
                                else delete row.default;
                                return true;
                              },
                              "",
                            )
                          }
                        />
                      </td>
                    </>
                  ) : null}
                  {nullable ? (
                    <td className={cell}>
                      <input
                        type="checkbox"
                        aria-label={`${noun[0].toUpperCase()}${noun.slice(1)} ${name(r, i)} accepts nulls`}
                        checked={r.nullable !== false}
                        onChange={(e) =>
                          editRow(
                            i,
                            (row) => {
                              if (e.target.checked) delete row.nullable;
                              else row.nullable = false;
                              return true;
                            },
                            "",
                          )
                        }
                      />
                    </td>
                  ) : null}
                  <td className={`${cell} whitespace-nowrap`}>
                    <Button
                      size="icon-row"
                      variant="ghost"
                      label={`Move ${noun} ${name(r, i)} up`}
                      disabled={i === 0}
                      onClick={() => update((j) => void moveRow(parentAt(j, path), member, i, -1))}
                    >
                      <ArrowUp />
                    </Button>
                    <Button
                      size="icon-row"
                      variant="ghost"
                      label={`Move ${noun} ${name(r, i)} down`}
                      disabled={i === rows.length - 1}
                      onClick={() => update((j) => void moveRow(parentAt(j, path), member, i, 1))}
                    >
                      <ArrowDown />
                    </Button>
                    <Button
                      size="icon-row"
                      variant="ghost"
                      label={keepOne && rows.length === 1 ? `A table result keeps at least one ${noun}` : `Delete ${noun} ${name(r, i)}`}
                      disabled={keepOne && rows.length === 1}
                      onClick={() => update((j) => removeRow(parentAt(j, path), member, i))}
                    >
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : empty ? (
        <EmptyState title={empty.title}>{empty.text}</EmptyState>
      ) : null}
      {problem ? (
        <p role="alert" className="text-12 text-danger">
          {problem}
        </p>
      ) : null}
    </div>
  );
}
