// The table editor's Exclusions and Storage tabs: exclusion constraints (each with its elements, method, predicate and when it
// is checked), and the table's storage parameters for its database's dialect (with what its stereotypes give it) and its
// partitioning (strategy, columns, partitions). Each change is one save of the table, so one undo step.
import { Plus, Trash2 } from "lucide-react";
import type { StereotypeDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { newId } from "@/lib/ids";
import { useVocabularies } from "@/inspector/fields";
import { PropertyBag } from "@/inspector/PropertyBag";
import { propertyText } from "@/inspector/propertyBag";
import {
  addExclusion,
  addExclusionElement,
  addPartition,
  editStorage,
  EXCLUSION_METHODS,
  exclusionText,
  inheritedStorage,
  PARTITION_STRATEGIES,
  partitionKeyProblem,
  POSTGRES_TABLE_PARAMETERS,
  postgresOnlyNote,
  removeExclusion,
  removeExclusionElement,
  removePartition,
  setExclusion,
  setExclusionElement,
  setPartition,
  setPartitionStrategy,
  storageKeyProblem,
  storageOf,
  togglePartitionColumn,
} from "@/workspaces/database/tableOptions";
import type { TableDoc } from "@/workspaces/database/useTableDoc";
import { FK_DEFERRABLE_LABELS } from "@/workspaces/database/fkEdits";
import { tableColumns } from "./databaseDocs";
import { CommitInput } from "./fields";
import { ColumnsPicker, useDialect } from "./TablePartsTabs";

type Rec = Record<string, unknown>;

const cell = "border-b border-subtle px-1 py-0.5 align-top";
const head = "border-b border-default px-1 py-0.5 text-left text-11 font-semibold text-secondary";

function Note({ text, testid }: { text: string | null; testid: string }) {
  return text ? (
    <p className="text-11 text-secondary" data-testid={testid}>
      {text}
    </p>
  ) : null;
}

/** The table's exclusion constraints: no two rows may match on every element (PostgreSQL EXCLUDE). */
export function ExclusionsTab({ td }: { td: TableDoc }) {
  const dialect = useDialect(td);
  const doc = td.doc as Rec | undefined;
  if (!doc) return null;
  const fixed = td.mode === "fixed";
  const columns = tableColumns(doc);
  const nameOf = (id: string) => columns.find((c) => c.id === id)?.name ?? id;
  const list = (doc.exclusions as Rec[] | undefined) ?? [];
  const tableName = td.table?.name ?? "table";
  const edit = (label: string, change: (d: Rec) => void | boolean) => void td.update(`${label} on ${tableName}`, (d) => change(d as Rec));
  return (
    <div className="flex max-w-4xl flex-col gap-2" data-testid="table-exclusions">
      <p className="text-12 text-secondary">
        No two rows may match on every element, each compared with its operator: a room with = and a period with &amp;&amp; for no overlapping reservations. Use
        one where a temporal key cannot say the rule (a predicate, another operator).
      </p>
      <Note text={postgresOnlyNote(dialect, "exclusion constraints")} testid="exclusions-note" />
      {!list.length ? <EmptyState title="No exclusion constraints">Add exclusion compares a column with = to start from.</EmptyState> : null}
      {list.map((x, i) => {
        const elements = (x.elements as Rec[] | undefined) ?? [];
        return (
          <div key={String(x.id ?? i)} className="flex flex-col gap-1 rounded-control border border-default p-1" data-testid={`exclusion-${i}`}>
            <div className="grid grid-cols-[minmax(0,1fr)_8rem_10rem_auto] items-end gap-1">
              <Field label="Name" htmlFor={`ex-${i}-name`}>
                <CommitInput
                  id={`ex-${i}-name`}
                  mono
                  value={String(x.name ?? "")}
                  placeholder={`ex_${tableName}_${elements.map((e) => (typeof e.column === "string" ? nameOf(e.column) : "expr")).join("_")}`}
                  onCommit={(v) => edit("Rename exclusion constraint", (d) => setExclusion(d, i, "name", v))}
                />
              </Field>
              <Field label="Method" htmlFor={`ex-${i}-method`}>
                <Select
                  id={`ex-${i}-method`}
                  disabled={fixed}
                  value={String(x.method ?? "gist")}
                  onChange={(e) => {
                    const value = e.target.value;
                    edit("Edit exclusion constraint", (d) => setExclusion(d, i, "method", value));
                  }}
                >
                  {EXCLUSION_METHODS.map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Checked" htmlFor={`ex-${i}-deferrable`}>
                <Select
                  id={`ex-${i}-deferrable`}
                  disabled={fixed}
                  value={String(x.deferrable ?? "not-deferrable")}
                  onChange={(e) => {
                    const value = e.target.value;
                    edit("Edit exclusion constraint", (d) => setExclusion(d, i, "deferrable", value));
                  }}
                >
                  {Object.entries(FK_DEFERRABLE_LABELS).map(([v, label]) => (
                    <option key={v} value={v}>
                      {label}
                    </option>
                  ))}
                </Select>
              </Field>
              <Button
                size="icon-row"
                variant="ghost"
                disabled={fixed}
                label={`Delete exclusion constraint ${String(x.name ?? i + 1)}`}
                onClick={() => edit("Delete exclusion constraint", (d) => removeExclusion(d, i))}
              >
                <Trash2 />
              </Button>
            </div>
            <table className="w-full border-collapse text-12">
              <thead>
                <tr>
                  <th scope="col" className={head}>
                    Column or expression
                  </th>
                  <th scope="col" className={`${head} w-40`}>
                    Operator class
                  </th>
                  <th scope="col" className={`${head} w-24`}>
                    Operator
                  </th>
                  <th scope="col" className={`${head} w-7`}>
                    <span className="sr-only">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {elements.map((e, j) => (
                  <tr key={j}>
                    <td className={cell}>
                      {typeof e.column === "string" ? (
                        <Select
                          aria-label={`Column of element ${j + 1}`}
                          className="h-6 text-12"
                          disabled={fixed}
                          value={e.column}
                          onChange={(ev) => {
                            const value = ev.target.value;
                            edit("Edit exclusion constraint", (d) => setExclusionElement(d, i, j, "column", value));
                          }}
                        >
                          {columns.map((c) => (
                            <option key={c.id} value={c.id}>
                              {c.name}
                            </option>
                          ))}
                        </Select>
                      ) : (
                        <CommitInput
                          label={`Expression of element ${j + 1}`}
                          mono
                          className="h-6 text-12"
                          value={String(e.expression ?? "")}
                          onCommit={(v) => edit("Edit exclusion constraint", (d) => setExclusionElement(d, i, j, "expression", v))}
                        />
                      )}
                    </td>
                    <td className={cell}>
                      <CommitInput
                        label={`Operator class of element ${j + 1}`}
                        mono
                        className="h-6 text-12"
                        value={String(e.operatorClass ?? "")}
                        placeholder="the type's"
                        onCommit={(v) => edit("Edit exclusion constraint", (d) => setExclusionElement(d, i, j, "operatorClass", v))}
                      />
                    </td>
                    <td className={cell}>
                      <CommitInput
                        label={`Operator of element ${j + 1}`}
                        mono
                        className="h-6 text-12"
                        value={String(e.operator ?? "")}
                        onCommit={(v) => edit("Edit exclusion constraint", (d) => setExclusionElement(d, i, j, "operator", v))}
                      />
                    </td>
                    <td className={cell}>
                      <Button
                        size="icon-row"
                        variant="ghost"
                        disabled={fixed || elements.length < 2}
                        label={`Remove element ${j + 1}`}
                        onClick={() => edit("Edit exclusion constraint", (d) => removeExclusionElement(d, i, j))}
                      >
                        <Trash2 />
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="flex flex-wrap items-center gap-1">
              <Button
                size="sm"
                variant="ghost"
                disabled={fixed || !columns.length}
                onClick={() => edit("Edit exclusion constraint", (d) => addExclusionElement(d, i, columns[0].id))}
              >
                <Plus /> Add column
              </Button>
              <Button
                size="sm"
                variant="ghost"
                disabled={fixed}
                onClick={() =>
                  edit("Edit exclusion constraint", (d) => {
                    if (!addExclusionElement(d, i, columns[0]?.id ?? "x")) return false;
                    const added = ((d.exclusions as Rec[])[i].elements as Rec[]).length - 1;
                    return setExclusionElement(d, i, added, "expression", `lower(${columns[0]?.name ?? "x"})`);
                  })
                }
              >
                <Plus /> Add expression
              </Button>
            </div>
            <Field label="Where (only the rows that satisfy it are compared)" htmlFor={`ex-${i}-where`}>
              <CommitInput
                id={`ex-${i}-where`}
                mono
                value={String(x.where ?? "")}
                placeholder="every row"
                onCommit={(v) => edit("Edit exclusion constraint", (d) => setExclusion(d, i, "where", v))}
              />
            </Field>
            <code className="font-mono text-11 text-secondary" data-testid={`exclusion-${i}-sql`}>
              {exclusionText(x, nameOf)}
            </code>
          </div>
        );
      })}
      <div>
        <Button
          size="sm"
          disabled={fixed || !columns.length}
          onClick={() => edit("New exclusion constraint", (d) => void addExclusion(d, newId(), columns[0].id))}
          data-testid="exclusion-add"
        >
          <Plus /> Add exclusion
        </Button>
      </div>
    </div>
  );
}

/** The table's storage parameters for its database's dialect, what its stereotypes give it, and its partitioning. */
export function StorageTab({ td }: { td: TableDoc }) {
  const dialect = useDialect(td);
  const vocab = useVocabularies("table");
  const doc = td.doc as Rec | undefined;
  if (!doc) return null;
  const fixed = td.mode === "fixed";
  const tableName = td.table?.name ?? "table";
  const columns = tableColumns(doc);
  const nameOf = (id: string) => columns.find((c) => c.id === id)?.name ?? id;
  const edit = (label: string, change: (d: Rec) => void | boolean) => void td.update(`${label} on ${tableName}`, (d) => change(d as Rec));
  const stereotypes = ((doc.stereotypes as string[] | undefined) ?? [])
    .map((k) => vocab.allStereotypes.find((s) => s.key === k) as StereotypeDoc | undefined)
    .filter((s): s is StereotypeDoc => !!s);
  const inherited = inheritedStorage(stereotypes as unknown as { key: string; storage?: unknown }[], dialect);
  const own = storageOf(doc, dialect);
  const by = doc.partitionBy as Rec | undefined;
  const partitions = (doc.partitions as Rec[] | undefined) ?? [];
  const keyProblem = partitionKeyProblem(doc, nameOf);
  return (
    <div className="flex max-w-3xl flex-col gap-2" data-testid="table-storage">
      <SectionTitle>Storage parameters ({dialect})</SectionTitle>
      {dialect === "sqlite" ? (
        <p className="text-12 text-secondary">SQLite has no storage parameters.</p>
      ) : (
        <>
          <p className="text-12 text-secondary">
            Written in the DDL of this database ({dialect === "mysql" ? "table options" : dialect === "oracle" ? "physical attributes" : "WITH (...)"}); a
            number or true/false as the dialect writes it, a text as SQL as it is.
            {by && dialect === "postgresql" ? " A partitioned table takes none: they are written on each partition." : ""}
          </p>
          {Object.keys(inherited).length ? (
            <ul className="flex flex-col gap-0.5 text-12" data-testid="storage-inherited">
              {Object.entries(inherited).map(([name, { value, from }]) => (
                <li key={name} className={name in own ? "text-secondary line-through" : ""}>
                  <span className="font-mono">
                    {name} = {propertyText(value)}
                  </span>{" "}
                  <span className="text-secondary">
                    from the stereotype {from}
                    {name in own ? ", overridden below" : ""}
                  </span>
                </li>
              ))}
            </ul>
          ) : null}
          <PropertyBag
            idPrefix={`table-storage-${tableName}`}
            title=""
            properties={own}
            options={{
              noun: "parameter",
              empty: Object.keys(inherited).length
                ? "No parameters of its own: the stereotypes' above apply."
                : "No storage parameters: the database's defaults apply.",
              types: ["number", "boolean", "text"],
              infer: true,
              keyProblem: storageKeyProblem,
              suggestions: dialect === "postgresql" ? POSTGRES_TABLE_PARAMETERS : undefined,
            }}
            onEdit={(change) => edit("Edit storage parameters", (d) => editStorage(d, dialect, change))}
          />
        </>
      )}
      <SectionTitle>Partitioning</SectionTitle>
      <Note text={postgresOnlyNote(dialect, "declarative partitioning")} testid="partitioning-note" />
      <div className="grid grid-cols-[10rem_minmax(0,1fr)] items-end gap-2">
        <Field label="Partitioned by" htmlFor="table-partition-strategy">
          <Select
            id="table-partition-strategy"
            disabled={fixed}
            value={String(by?.strategy ?? "")}
            onChange={(e) => {
              const value = e.target.value;
              edit("Edit partitioning", (d) => setPartitionStrategy(d, value || undefined, columns[0]?.id));
            }}
          >
            <option value="">Not partitioned</option>
            {PARTITION_STRATEGIES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </Select>
        </Field>
        {by ? (
          <Field label="Partition columns">
            <ColumnsPicker
              label="Partition columns"
              options={columns}
              chosen={(by.columns as string[] | undefined) ?? []}
              disabled={fixed}
              onToggle={(c) => edit("Edit partitioning", (d) => togglePartitionColumn(d, c))}
            />
          </Field>
        ) : null}
      </div>
      {keyProblem ? (
        <p role="alert" className="text-12 text-danger" data-testid="partition-key-problem">
          {keyProblem}
        </p>
      ) : null}
      {by ? (
        <>
          <table className="w-full border-collapse text-12" data-testid="table-partitions">
            <thead>
              <tr>
                <th scope="col" className={`${head} w-56`}>
                  Partition
                </th>
                <th scope="col" className={head}>
                  Bounds (after FOR VALUES)
                </th>
                <th scope="col" className={`${head} w-7`}>
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {partitions.map((p, i) => (
                <tr key={String(p.id ?? i)}>
                  <td className={cell}>
                    <CommitInput
                      label={`Name of partition ${i + 1}`}
                      mono
                      className="h-6 text-12"
                      value={String(p.name ?? "")}
                      onCommit={(v) => edit("Rename partition", (d) => setPartition(d, i, "name", v))}
                    />
                  </td>
                  <td className={cell}>
                    {p.default === true ? (
                      <span className="text-secondary">DEFAULT: the rows no other partition takes</span>
                    ) : (
                      <CommitInput
                        label={`Bounds of partition ${i + 1}`}
                        mono
                        className="h-6 text-12"
                        value={String(p.bounds ?? "")}
                        onCommit={(v) => edit("Edit partition", (d) => setPartition(d, i, "bounds", v))}
                      />
                    )}
                  </td>
                  <td className={cell}>
                    <Button
                      size="icon-row"
                      variant="ghost"
                      disabled={fixed}
                      label={`Delete partition ${String(p.name ?? i + 1)}`}
                      onClick={() => edit("Delete partition", (d) => removePartition(d, i))}
                    >
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="flex gap-1">
            <Button
              size="sm"
              disabled={fixed}
              onClick={() => edit("New partition", (d) => void addPartition(d, newId(), tableName))}
              data-testid="partition-add"
            >
              <Plus /> Add partition
            </Button>
            {by.strategy !== "hash" && !partitions.some((p) => p.default === true) ? (
              <Button size="sm" variant="ghost" disabled={fixed} onClick={() => edit("New partition", (d) => void addPartition(d, newId(), tableName, true))}>
                <Plus /> Add default partition
              </Button>
            ) : null}
          </div>
          <p className="text-11 text-secondary">
            Partitions made as time goes by (a month at a time) are an operational job: set it up with a SQL object (pg_partman, or your own job).
          </p>
        </>
      ) : null}
    </div>
  );
}
