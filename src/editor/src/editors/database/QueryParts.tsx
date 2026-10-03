// The sections of the query editor, shared by the query and by each collection's nested query: Sources (from and joins, a
// join's condition from a foreign key), Select (one row per attribute of the result entity, or the fields of an ad hoc row; the
// list filled from same-named columns), Filter (the where tree), Group and order (grouping, having, ordering, distinct and, at
// the top, paging), Parameters and Collections. Each gesture is one `update` (one save, one undo step).
import { useMemo, useState } from "react";
import type { QueryClient } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, KeyRound, Plus, Trash2, Wand2 } from "lucide-react";
import { elementQuery, useElements, useIndex } from "@/api/queries";
import type { DatabaseView, ElementSummary, EntityDoc, QueryView, RelationDoc, StereotypeDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { useVocabularies } from "@/inspector/fields";
import { indexLookup } from "@/model/index";
import { BUILTIN_TYPES } from "@/model/model";
import {
  defaultAlias,
  fieldName,
  fillFromColumns,
  findSource,
  foreignKeyJoins,
  JOIN_KINDS,
  nameProblem,
  renameAlias,
  renameParameter,
  scopeOf,
  sourceOptions,
  tidy,
  withAttributeField,
  type AttributeOption,
  type Collection,
  type Expression,
  type Field,
  type JoinKind,
  type Order,
  type QueryDoc,
  type QueryParameter,
  type ScopeAlias,
  type SourceOption,
  type Subquery,
  type TableFileRow,
} from "@/model/queryTree";
import { facetProblem, setFacet, type TypeFacet } from "./databaseDocs";
import { CommitInput } from "./fields";
import { cellSelect, ExpressionEditor, firstColumn, ParamSelect, type QueryEnv } from "./queryExpression";
import { PredicateEditor } from "./queryPredicate";
import { TypeSelect, useDatabaseTypes } from "./TypedRowsGrid";

/** Edits a query body (the query, or a collection's nested query) as one gesture. */
export type BodyUpdate = (mutate: (body: Subquery) => void) => void;

/** What the sections read besides the body: the resolved database, its table files, and the query's environment. */
export interface QueryContext {
  view: DatabaseView | null;
  files: readonly TableFileRow[];
  /** The query's parameters, routines and dialect; `scope` is replaced per body. */
  env: QueryEnv;
  /** The aliases of the queries around this body (a collection's correlation). */
  outer: readonly ScopeAlias[];
}

const head = "h-6 px-1 text-left text-11 font-semibold text-secondary";
const cell = "px-1 py-0.5 align-top";

function Problem({ text }: { text: string | null }) {
  return text ? (
    <p role="alert" className="text-12 text-danger">
      {text}
    </p>
  ) : null;
}

/** The index's table files of a database (a synthesized table's overlay names its entity), for reading a source id. */
export function tableFiles(rows: readonly ElementSummary[] | undefined, database: string): TableFileRow[] {
  return (rows ?? [])
    .filter((r) => r.kind === "table" && r.database === database)
    .map((r) => ({ id: r.id, entity: r.entity ?? null, relation: (r as { relation?: string | null }).relation ?? null }));
}

// ------------------------------------------------------------------ entities

export interface AttributeInfo extends AttributeOption {
  required: boolean;
  collection: boolean;
  /** The attribute spans several columns (a value object): one field cannot fill it. */
  spans: boolean;
}

/**
 * What else a field of an entity's row may fill besides an attribute: a member of a value object attribute (written
 * `attributeId.memberId`) or the foreign key of a to-one navigation (written as the relation end it leads to).
 */
export interface FillTarget extends AttributeOption {
  kind: "member" | "foreign-key";
  /** A field name for it (`totalAmount`, `customerId`), when a field keeps its value under a name of its own. */
  fieldName: string;
}

export interface CollectionTarget {
  /** What a collection's `attribute` is written as: a collection attribute's id, or the relation end a navigation leads to. */
  value: string;
  label: string;
  /** The attribute's or the navigation's own name (what an ad hoc collection is named after the result entity goes). */
  name: string;
  /** The entity each element has, when the target says (a navigation's). */
  entity: string | null;
}

export interface EntityShape {
  attributes: AttributeInfo[];
  /** The value object members and foreign keys a field may fill, after the attributes. */
  fills: FillTarget[];
  collections: CollectionTarget[];
  key: AttributeOption | null;
  /** Whether the documents the shape reads are all loaded (until then, a field's attribute is not called unknown). */
  ready: boolean;
}

type ShapeDoc = { kind?: string; name?: string; attributes?: { id: string; name: string; type?: unknown; collection?: boolean }[] };

/** The entity and its bases (root first), by the index's base links. */
function chainOf(entityId: string | null | undefined, lookup: ReturnType<typeof indexLookup>): string[] {
  const ids: string[] = [];
  for (let id = entityId ?? undefined; id && !ids.includes(id); id = lookup.byId.get(id)?.base ?? undefined) ids.unshift(id);
  return ids;
}

/** The relations with an end at one of the entities. */
const relationsOf = (rows: readonly ElementSummary[] | undefined, chain: readonly string[]) =>
  (rows ?? []).filter((r) => r.kind === "relation" && (r.ends ?? []).some((e) => chain.includes(e.entity))).map((r) => r.id);

const refOf = (type: unknown) => (typeof type === "object" && type ? (type as { ref?: string }).ref : undefined);

/** The value objects the chain's attributes (and its stereotypes') hold. */
function valueObjectsOf(
  chain: readonly string[],
  docs: ReadonlyMap<string, { json: unknown }>,
  stereotypes: readonly StereotypeDoc[],
  lookup: ReturnType<typeof indexLookup>,
): string[] {
  const ids: string[] = [];
  for (const id of chain) {
    const entity = docs.get(id)?.json as EntityDoc | undefined;
    const own = [...(entity?.attributes ?? []), ...(entity?.stereotypes ?? []).flatMap((s) => stereotypes.find((x) => x.key === s)?.attributes ?? [])];
    for (const a of own) {
      const ref = refOf(a.type);
      if (ref && lookup.byId.get(ref)?.kind === "value-object" && !ids.includes(ref)) ids.push(ref);
    }
  }
  return ids;
}

/**
 * An entity's shape from its documents (pure): its attributes (its bases' first, each entity's stereotype attributes after its
 * own), the value object members and foreign keys a field may fill, and its to-many targets. Only an end with a navigation name
 * is a navigation (the engine's rule): an unnamed end is neither a collection target nor a foreign key a field fills.
 */
export function entityShapeOf(
  chain: readonly string[],
  relationIds: readonly string[],
  docs: ReadonlyMap<string, { json: unknown }>,
  stereotypes: readonly StereotypeDoc[],
  lookup: ReturnType<typeof indexLookup>,
): Omit<EntityShape, "ready"> {
  const attributes: AttributeInfo[] = [];
  const fills: FillTarget[] = [];
  let key: AttributeOption | null = null;
  for (const id of chain) {
    const entity = docs.get(id)?.json as EntityDoc | undefined;
    if (!entity) continue;
    const own = [...(entity.attributes ?? [])];
    for (const s of entity.stereotypes ?? []) own.push(...(stereotypes.find((x) => x.key === s)?.attributes ?? []));
    for (const a of own) {
      const ref = refOf(a.type);
      const spans = !!ref && lookup.byId.get(ref)?.kind === "value-object";
      attributes.push({ id: a.id, name: a.name, required: a.required === true, collection: a.collection === true, spans });
      if (spans && a.collection !== true)
        for (const m of (docs.get(ref!)?.json as ShapeDoc | undefined)?.attributes ?? [])
          if (m.collection !== true && !(refOf(m.type) && lookup.byId.get(refOf(m.type)!)?.kind === "value-object"))
            fills.push({
              id: `${a.id}.${m.id}`,
              name: `${a.name}.${m.name}`,
              kind: "member",
              fieldName: a.name + m.name.charAt(0).toUpperCase() + m.name.slice(1),
            });
    }
    const keyId = entity.key?.attributes?.[0];
    if (keyId && !key) key = attributes.find((a) => a.id === keyId) ?? null;
  }
  const collections: CollectionTarget[] = attributes.filter((a) => a.collection).map((a) => ({ value: a.id, label: a.name, name: a.name, entity: null }));
  for (const rid of relationIds) {
    const relation = docs.get(rid)?.json as RelationDoc | undefined;
    const ends = relation?.ends ?? [];
    ends.forEach((end, i) => {
      const other = ends[1 - i];
      if (!other || !chain.includes(end.entity)) return;
      const navigation = (other as { navigation?: string }).navigation;
      if (!navigation) return;
      const max = (other as { max?: number | "*" }).max;
      const target = lookup.byId.get(other.entity)?.name ?? other.entity;
      if (max !== undefined && max !== "*" && max <= 1)
        fills.push({ id: other.id, name: `${navigation} (foreign key to ${target})`, kind: "foreign-key", fieldName: `${navigation}Id` });
      else collections.push({ value: other.id, label: `${navigation} (${target})`, name: navigation, entity: other.entity });
    });
  }
  return { attributes, fills, collections, key };
}

/** An entity's shape (entityShapeOf over the documents it reads). */
export function useEntityShape(entityId: string | null | undefined): EntityShape {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const chain = useMemo(() => chainOf(entityId, lookup), [entityId, lookup]);
  const relationIds = useMemo(() => relationsOf(index.data, chain), [index.data, chain]);
  const docs = useElements([...chain, ...relationIds]);
  const vocab = useVocabularies("entity");
  const stereotypes = vocab.allStereotypes as StereotypeDoc[];
  const valueObjects = useMemo(() => valueObjectsOf(chain, docs.byId, stereotypes, lookup), [chain, docs.byId, stereotypes, lookup]);
  const members = useElements(valueObjects);
  return useMemo(() => {
    const all = new Map<string, { json: unknown }>([...docs.byId, ...members.byId]);
    const ready = [...chain, ...relationIds, ...valueObjects].every((id) => all.has(id));
    return { ...entityShapeOf(chain, relationIds, all, stereotypes, lookup), ready };
  }, [chain, relationIds, valueObjects, docs.byId, members.byId, stereotypes, lookup]);
}

/** An entity's shape read now (the documents fetched through the cache), for a gesture that needs another entity's shape. */
export async function loadEntityShape(
  qc: QueryClient,
  entityId: string,
  rows: readonly ElementSummary[] | undefined,
  stereotypes: readonly StereotypeDoc[],
): Promise<Omit<EntityShape, "ready">> {
  const lookup = indexLookup(rows);
  const chain = chainOf(entityId, lookup);
  const relationIds = relationsOf(rows, chain);
  const docs = new Map<string, { json: unknown }>();
  const load = async (ids: readonly string[]) =>
    (await Promise.all(ids.map(async (id) => [id, await qc.fetchQuery(elementQuery(id)).catch(() => null)] as const))).forEach(
      ([id, d]) => d && docs.set(id, d),
    );
  await load([...chain, ...relationIds]);
  await load(valueObjectsOf(chain, docs, stereotypes, lookup));
  return entityShapeOf(chain, relationIds, docs, stereotypes, lookup);
}

// ------------------------------------------------------------------ sources

/** A source as the pickers name it: `schema.name`, a view marked. */
const sourceLabel = (o: SourceOption | null): string | null =>
  o ? `${o.schema ? `${o.schema}.${o.name}` : o.name}${o.kind === "view" ? " (view)" : ""}` : null;

/** The from source and the joins: what each reads, its alias, the join's kind and condition. */
export function SourcesSection({ body, update, q, label }: { body: Subquery; update: BodyUpdate; q: QueryContext; label: string }) {
  const options = sourceOptions(q.view);
  const joins = body.joins ?? [];
  const [problem, setProblem] = useState<string | null>(null);
  const scopeUpTo = (n: number) => scopeOf({ from: body.from, joins: joins.slice(0, n) }, q.view, q.files, q.outer);
  const aliases = [body.from, ...joins].map((s, i) => s.alias ?? scopeUpTo(i + 1)[i]?.alias ?? s.source);
  const sourceSelect = (value: string, aria: string, onPick: (v: string) => void) => (
    <select aria-label={aria} className={`${cellSelect} w-56 font-sans`} value={value} onChange={(e) => onPick(e.target.value)}>
      {options.some((o) => o.value === value) ? null : (
        <option value={value}>{sourceLabel(findSource(q.view, value, q.files)) ?? `${value || "(pick a source)"}`}</option>
      )}
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {sourceLabel(o)}
        </option>
      ))}
    </select>
  );
  const aliasInput = (i: number, aria: string) => (
    <CommitInput
      label={aria}
      mono
      className="h-6 w-24 text-12"
      value={aliases[i]}
      onCommit={(v) => {
        const next = v.trim();
        const why = nameProblem(
          next,
          aliases.filter((_, j) => j !== i),
          "alias",
        );
        setProblem(why);
        if (why) return;
        update((b) => {
          const from = aliases[i];
          const target = i === 0 ? b.from : b.joins![i - 1];
          target.alias = from;
          renameAlias(b, from, next);
          target.alias = next;
        });
      }}
    />
  );
  const addJoin = () => {
    // A table that shares a foreign key with one already read comes first; else the first source not read yet.
    const scope = scopeUpTo(joins.length + 1);
    const read = new Set([body.from.source, ...joins.map((j) => j.source)]);
    const related = options.find(
      (o) =>
        !read.has(o.value) &&
        o.table &&
        foreignKeyJoins(
          { alias: "x", source: o, columns: [], outer: false },
          scope.filter((s) => !s.outer),
        ).length > 0,
    );
    const pick = related ?? options.find((o) => !read.has(o.value)) ?? options[0];
    if (!pick) return;
    update((b) => void (b.joins = [...(b.joins ?? []), { source: pick.value, alias: defaultAlias(pick.name, aliases) }]));
  };
  return (
    <div className="flex flex-col gap-2" data-testid="query-sources">
      <div className="flex flex-wrap items-center gap-2" data-testid="query-from">
        <span className="w-12 text-12 font-medium text-secondary">From</span>
        {sourceSelect(body.from.source, `${label} from source`, (v) => update((b) => void (b.from = { ...b.from, source: v })))}
        <span className="text-12 text-secondary">as</span>
        {aliasInput(0, `${label} from alias`)}
      </div>
      {joins.map((j, i) => {
        const joined = scopeUpTo(i + 1).find((s) => s.alias === aliases[i + 1]);
        const earlier = scopeUpTo(i + 1).filter((s) => !s.outer && s.alias !== aliases[i + 1]);
        const candidates = joined ? foreignKeyJoins(joined, earlier) : [];
        const kind = j.kind ?? "inner";
        const setOn = (on: QueryDoc["where"]) =>
          update((b) => {
            const target = b.joins![i];
            if (on) target.on = on;
            else delete target.on;
          });
        const env: QueryEnv = { ...q.env, scope: scopeUpTo(i + 2) };
        return (
          <div key={i} className="flex flex-col gap-1 rounded-[4px] border border-default p-1" data-testid={`query-join-${i + 1}`}>
            <div className="flex flex-wrap items-center gap-2">
              <select
                aria-label={`${label} join ${i + 1} kind`}
                className={`${cellSelect} w-20 font-sans`}
                value={kind}
                onChange={(e) =>
                  update((b) => {
                    const target = b.joins![i];
                    if (e.target.value === "inner") delete target.kind;
                    else target.kind = e.target.value as JoinKind;
                    if (target.kind === "cross") delete target.on;
                  })
                }
              >
                {JOIN_KINDS.map((k) => (
                  <option key={k} value={k}>
                    {k} join
                  </option>
                ))}
              </select>
              {sourceSelect(j.source, `${label} join ${i + 1} source`, (v) => update((b) => void (b.joins![i] = { ...b.joins![i], source: v })))}
              <span className="text-12 text-secondary">as</span>
              {aliasInput(i + 1, `${label} join ${i + 1} alias`)}
              {kind !== "cross" ? (
                candidates.length > 1 ? (
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <Button size="sm" variant="ghost" title="Fill the condition from a foreign key between this source and one before it">
                        <KeyRound /> Join by foreign key
                      </Button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="start">
                      {candidates.map((c) => (
                        <DropdownMenuItem key={c.label} onSelect={() => setOn(c.on)}>
                          {c.label}
                        </DropdownMenuItem>
                      ))}
                    </DropdownMenuContent>
                  </DropdownMenu>
                ) : (
                  <Button
                    size="sm"
                    variant="ghost"
                    disabled={!candidates.length}
                    title={
                      candidates.length
                        ? `Fill the condition from the foreign key: ${candidates[0].label}`
                        : "No foreign key links this source with the ones before it"
                    }
                    onClick={() => setOn(candidates[0].on)}
                  >
                    <KeyRound /> Join by foreign key
                  </Button>
                )
              ) : null}
              <span className="flex-1" />
              <Button
                size="icon-row"
                variant="ghost"
                label={`Remove join ${i + 1}`}
                onClick={() =>
                  update((b) => {
                    b.joins = b.joins!.filter((_, x) => x !== i);
                    tidy(b);
                  })
                }
              >
                <Trash2 />
              </Button>
            </div>
            {kind !== "cross" ? (
              <div className="ml-2 flex items-start gap-2">
                <span className="w-6 pt-1 text-12 text-secondary">on</span>
                <PredicateEditor value={j.on} env={env} label={`${label} join ${i + 1} on`} empty="No condition yet." onChange={setOn} />
              </div>
            ) : null}
          </div>
        );
      })}
      <Problem text={problem} />
      <Button size="sm" variant="ghost" className="self-start" onClick={addJoin} disabled={!options.length} data-testid="query-add-join">
        <Plus /> Add join
      </Button>
    </div>
  );
}

// ------------------------------------------------------------------ select

/**
 * The select list. With a result entity, one row per attribute (a value object spans several columns: one row per member instead),
 * per to-one navigation's foreign key, its expression or "not selected", plus the fields of the entity's own name and any field
 * naming what the entity does not have (with Remove); without one, the fields of the ad hoc row.
 */
export function SelectSection({
  body,
  update,
  q,
  entity,
  label,
  resolved,
}: {
  body: Subquery;
  update: BodyUpdate;
  q: QueryContext;
  entity: string | null;
  label: string;
  /** The resolved fields (the database view's), for their inferred types. */
  resolved?: QueryView["select"];
}) {
  const shape = useEntityShape(entity);
  const select = body.select ?? [];
  const [problem, setProblem] = useState<string | null>(null);
  const scope = scopeOf(body, q.view, q.files, q.outer);
  const env: QueryEnv = { ...q.env, scope };
  const own = scope.filter((s) => !s.outer);
  // One row per attribute one field fills, then per value object member and foreign key (the order fields take).
  const attributes = [...shape.attributes.filter((a) => !a.collection && !a.spans), ...shape.fills.map((f) => ({ ...f, required: false }))];
  const known = new Set([...shape.attributes.map((a) => a.id), ...shape.fills.map((f) => f.id)]);
  /** A field naming what the entity does not have (after a change of the result entity, or a hand edit): listed with Remove. */
  const unknown = (f: Field) => !!entity && shape.ready && !!f.attribute && !known.has(f.attribute);
  const typeOf = (f: Field) => {
    const r = resolved?.find((x) => (f.attribute ? x.attributeId === f.attribute : x.name === f.name));
    return r ? `${r.type ?? "?"}${r.nullable ? "?" : ""}` : "";
  };
  const setField = (i: number, field: Field) => update((b) => void (b.select = (b.select ?? []).map((f, j) => (j === i ? field : f))));
  const removeField = (i: number) => update((b) => void (b.select = (b.select ?? []).filter((_, j) => j !== i)));
  const fill = (alias: ScopeAlias) =>
    update((b) => {
      b.select = fillFromColumns(b.select ?? [], attributes, alias).select;
    });
  const extra = select.map((f, i) => ({ f, i })).filter(({ f }) => !entity || !f.attribute || unknown(f));
  const addField = () =>
    update((b) => {
      const taken = (b.select ?? []).map((f) => f.name ?? "");
      const first = firstColumn(env);
      const column = first ? own.flatMap((s) => s.columns.map((c) => ({ s, c }))).find(({ s, c }) => `${s.alias}.${c.ref}` === first) : undefined;
      b.select = [...(b.select ?? []), { name: fieldName(column?.c.name ?? "value", taken), expression: first ? { column: first } : { value: 1 } }];
    });
  return (
    <div className="flex flex-col gap-2" data-testid="query-select">
      <div className="flex flex-wrap items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          {entity
            ? "Each row fills the older result shape's members (see General): pick the column or expression for each; a field of its own name holds any other value."
            : "Each field is a member of the row: its name, its expression and, when the expression does not say, its type."}
        </p>
        {entity && own.length ? (
          own.length > 1 ? (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button size="sm" variant="ghost" title="Fill each member without a field from the same-named column of a source">
                  <Wand2 /> Fill from columns by name
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                {own.map((s) => (
                  <DropdownMenuItem key={s.alias} onSelect={() => fill(s)}>
                    {s.alias} ({s.source?.name ?? "?"})
                  </DropdownMenuItem>
                ))}
              </DropdownMenuContent>
            </DropdownMenu>
          ) : (
            <Button
              size="sm"
              variant="ghost"
              title={`Fill each member without a field from the same-named column of ${own[0].alias}`}
              onClick={() => fill(own[0])}
            >
              <Wand2 /> Fill from columns by name
            </Button>
          )
        ) : null}
        <Button size="sm" variant="ghost" onClick={addField} title={entity ? "Add a field of its own name" : "Add a field to the row"}>
          <Plus /> Add field
        </Button>
      </div>
      {entity ? (
        <table className="w-full table-fixed border-collapse text-12" aria-label={`${label} attributes`}>
          <thead>
            <tr>
              <th scope="col" className={`${head} w-40`}>
                Attribute
              </th>
              <th scope="col" className={head}>
                Expression
              </th>
              <th scope="col" className={`${head} w-24`}>
                Type
              </th>
              <th scope="col" className="w-8">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {attributes.map((a) => {
              const at = select.findIndex((f) => f.attribute === a.id);
              const field = at >= 0 ? select[at] : undefined;
              return (
                <tr key={a.id} className="border-t border-default" data-testid={`query-attribute-${a.name}`}>
                  <td className={`${cell} pt-1`}>
                    <span className={field ? "font-medium" : "text-secondary"}>{a.name}</span>
                    {a.required ? <span className="text-11 text-secondary"> required</span> : null}
                  </td>
                  <td className={cell}>
                    {field ? (
                      <ExpressionEditor
                        value={field.expression}
                        env={env}
                        label={`Field ${a.name}`}
                        onChange={(e) => setField(at, { ...field, expression: e })}
                      />
                    ) : (
                      <Button
                        size="sm"
                        variant="ghost"
                        title={`Select a value for ${a.name}`}
                        onClick={() =>
                          update((b) => {
                            const first = firstColumn(env);
                            b.select = withAttributeField(b.select ?? [], attributes, {
                              attribute: a.id,
                              expression: first ? { column: first } : { null: true },
                            });
                          })
                        }
                      >
                        <Plus /> Select {a.name}
                      </Button>
                    )}
                  </td>
                  <td className={`${cell} pt-1 font-mono text-11 text-secondary`}>{field ? typeOf(field) : "not selected"}</td>
                  <td className={cell}>
                    {field ? (
                      <Button size="icon-row" variant="ghost" label={`Leave ${a.name} out of the select list`} onClick={() => removeField(at)}>
                        <Trash2 />
                      </Button>
                    ) : null}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      ) : null}
      {extra.length ? (
        <table className="w-full table-fixed border-collapse text-12" aria-label={`${label} fields`}>
          <thead>
            <tr>
              <th scope="col" className={`${head} w-40`}>
                Name
              </th>
              <th scope="col" className={head}>
                Expression
              </th>
              {entity ? null : (
                <>
                  <th scope="col" className={`${head} w-28`}>
                    Declared type
                  </th>
                  <th scope="col" className={`${head} w-20`}>
                    Nulls
                  </th>
                </>
              )}
              <th scope="col" className={`${head} w-24`}>
                Type
              </th>
              <th scope="col" className="w-[4.5rem]">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {extra.map(({ f, i }, n) => {
              const name = f.name ?? f.attribute ?? String(i + 1);
              return (
                <tr key={i} className="border-t border-default" data-testid={`query-field-${name}`}>
                  <td className={cell}>
                    {unknown(f) ? (
                      <span
                        className="block truncate pt-1 font-mono text-11 text-danger"
                        title={`The field fills ${f.attribute}, which the result shape does not have: remove it, or select the value again for a member above.`}
                        data-testid="query-field-unknown"
                      >
                        {f.name ?? f.attribute} (not in the shape)
                      </span>
                    ) : (
                      <CommitInput
                        label={`Name of field ${n + 1}`}
                        mono
                        className="h-6 text-12"
                        value={f.name ?? ""}
                        onCommit={(v) => {
                          const next = v.trim();
                          const why = nameProblem(
                            next,
                            select.filter((_, j) => j !== i).map((x) => x.name ?? ""),
                            "field",
                          );
                          setProblem(why);
                          if (!why) setField(i, { ...f, name: next });
                        }}
                      />
                    )}
                  </td>
                  <td className={cell}>
                    <ExpressionEditor value={f.expression} env={env} label={`Field ${name}`} onChange={(e) => setField(i, { ...f, expression: e })} />
                  </td>
                  {entity ? null : (
                    <>
                      <td className={cell}>
                        <select
                          aria-label={`Declared type of field ${name}`}
                          className={`${cellSelect} w-full`}
                          value={f.type ?? ""}
                          onChange={(e) => {
                            const next = { ...f };
                            if (e.target.value) next.type = e.target.value;
                            else delete next.type;
                            setField(i, next);
                          }}
                        >
                          <option value="">(inferred)</option>
                          {BUILTIN_TYPES.map((t) => (
                            <option key={t} value={t}>
                              {t}
                            </option>
                          ))}
                        </select>
                      </td>
                      <td className={cell}>
                        <select
                          aria-label={`Nulls of field ${name}`}
                          className={`${cellSelect} w-full font-sans`}
                          value={f.nullable === undefined ? "" : String(f.nullable)}
                          onChange={(e) => {
                            const next = { ...f };
                            if (e.target.value === "") delete next.nullable;
                            else next.nullable = e.target.value === "true";
                            setField(i, next);
                          }}
                        >
                          <option value="">(inferred)</option>
                          <option value="true">may be null</option>
                          <option value="false">never null</option>
                        </select>
                      </td>
                    </>
                  )}
                  <td className={`${cell} pt-1 font-mono text-11 text-secondary`}>{typeOf(f)}</td>
                  <td className={`${cell} whitespace-nowrap`}>
                    <MoveButtons
                      noun={`field ${name}`}
                      first={n === 0}
                      last={n === extra.length - 1}
                      move={(by) =>
                        update((b) => {
                          const list = [...(b.select ?? [])];
                          const to = extra[n + by]?.i;
                          if (to === undefined) return;
                          [list[i], list[to]] = [list[to], list[i]];
                          b.select = list;
                        })
                      }
                    />
                    <Button size="icon-row" variant="ghost" label={`Remove field ${name}`} onClick={() => removeField(i)}>
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      ) : entity ? null : (
        <EmptyState title="No fields">Add a field: each is a member of the result row.</EmptyState>
      )}
      <Problem text={problem} />
    </div>
  );
}

function MoveButtons({ noun, first, last, move }: { noun: string; first: boolean; last: boolean; move: (by: -1 | 1) => void }) {
  return (
    <>
      <Button size="icon-row" variant="ghost" label={`Move ${noun} up`} disabled={first} onClick={() => move(-1)}>
        <ArrowUp />
      </Button>
      <Button size="icon-row" variant="ghost" label={`Move ${noun} down`} disabled={last} onClick={() => move(1)}>
        <ArrowDown />
      </Button>
    </>
  );
}

// ------------------------------------------------------------------ filter

export function FilterSection({ body, update, q, label }: { body: Subquery; update: BodyUpdate; q: QueryContext; label: string }) {
  const env: QueryEnv = { ...q.env, scope: scopeOf(body, q.view, q.files, q.outer) };
  return (
    <div className="flex flex-col gap-1" data-testid="query-filter">
      <p className="text-12 text-secondary">
        The rows the query keeps. A comparison takes a column, a parameter, a value, a function call or SQL per dialect on each side
        {q.outer.length ? "; the aliases of the query around this one tie its rows to the parent's (equalities at the top)" : ""}.
      </p>
      <PredicateEditor
        value={body.where}
        env={env}
        label={label}
        empty="No condition: every row."
        onChange={(next) =>
          update((b) => {
            if (next) b.where = next;
            else delete b.where;
          })
        }
      />
    </div>
  );
}

// ------------------------------------------------------------------ group and order

const PAGING_LABELS = { offset: "Offset (rows to skip)", limit: "Limit (most rows)" } as const;

export function GroupOrderSection({
  body,
  update,
  q,
  label,
  paging,
}: {
  body: Subquery;
  update: BodyUpdate;
  q: QueryContext;
  label: string;
  /** The query's paging (the top level only), with its own update. */
  paging?: { value: QueryDoc["paging"]; update: (mutate: (doc: QueryDoc) => void) => void };
}) {
  const env: QueryEnv = { ...q.env, scope: scopeOf(body, q.view, q.files, q.outer) };
  const groupBy = body.groupBy ?? [];
  const orderBy = body.orderBy ?? [];
  const blank = (): Expression => (firstColumn(env) ? { column: firstColumn(env)! } : { value: 1 });
  const setOrder = (i: number, order: Order) => update((b) => void (b.orderBy = (b.orderBy ?? []).map((o, j) => (j === i ? order : o))));
  return (
    <div className="flex flex-col gap-2" data-testid="query-group-order">
      <label className="flex items-center gap-2 text-13">
        <input
          type="checkbox"
          checked={body.distinct === true}
          onChange={(e) =>
            update((b) => {
              if (e.target.checked) b.distinct = true;
              else delete b.distinct;
            })
          }
        />
        Distinct (remove duplicate rows)
      </label>
      <SectionTitle>Group by</SectionTitle>
      {groupBy.map((g, i) => (
        <div key={i} className="flex items-start gap-1" data-testid={`query-group-${i + 1}`}>
          <ExpressionEditor
            value={g}
            env={env}
            label={`${label} group ${i + 1}`}
            onChange={(e) => update((b) => void (b.groupBy = (b.groupBy ?? []).map((x, j) => (j === i ? e : x))))}
          />
          <Button
            size="icon-row"
            variant="ghost"
            label={`Remove grouping ${i + 1}`}
            onClick={() =>
              update((b) => {
                b.groupBy = (b.groupBy ?? []).filter((_, j) => j !== i);
                tidy(b);
              })
            }
          >
            <Trash2 />
          </Button>
        </div>
      ))}
      <Button size="sm" variant="ghost" className="self-start" onClick={() => update((b) => void (b.groupBy = [...(b.groupBy ?? []), blank()]))}>
        <Plus /> Add grouping
      </Button>
      <SectionTitle>Having</SectionTitle>
      <PredicateEditor
        value={body.having}
        env={env}
        label={`${label} having`}
        empty="No condition on the groups."
        onChange={(next) =>
          update((b) => {
            if (next) b.having = next;
            else delete b.having;
          })
        }
      />
      <SectionTitle>Order by</SectionTitle>
      {orderBy.map((o, i) => (
        <div key={i} className="flex flex-wrap items-start gap-1" data-testid={`query-order-${i + 1}`}>
          <ExpressionEditor value={o.expression} env={env} label={`${label} order ${i + 1}`} onChange={(e) => setOrder(i, { ...o, expression: e })} />
          <select
            aria-label={`${label} order ${i + 1} direction`}
            className={`${cellSelect} font-sans`}
            value={o.direction ?? "asc"}
            onChange={(e) => {
              const next = { ...o };
              if (e.target.value === "desc") next.direction = "desc";
              else delete next.direction;
              setOrder(i, next);
            }}
          >
            <option value="asc">ascending</option>
            <option value="desc">descending</option>
          </select>
          <select
            aria-label={`${label} order ${i + 1} nulls`}
            className={`${cellSelect} font-sans`}
            value={o.nulls ?? ""}
            onChange={(e) => {
              const next = { ...o };
              if (e.target.value) next.nulls = e.target.value as "first" | "last";
              else delete next.nulls;
              setOrder(i, next);
            }}
          >
            <option value="">nulls: the database&apos;s order</option>
            <option value="first">nulls first</option>
            <option value="last">nulls last</option>
          </select>
          <MoveButtons
            noun={`order ${i + 1}`}
            first={i === 0}
            last={i === orderBy.length - 1}
            move={(by) =>
              update((b) => {
                const list = [...(b.orderBy ?? [])];
                [list[i], list[i + by]] = [list[i + by], list[i]];
                b.orderBy = list;
              })
            }
          />
          <Button
            size="icon-row"
            variant="ghost"
            label={`Remove order ${i + 1}`}
            onClick={() =>
              update((b) => {
                b.orderBy = (b.orderBy ?? []).filter((_, j) => j !== i);
                tidy(b);
              })
            }
          >
            <Trash2 />
          </Button>
        </div>
      ))}
      <Button
        size="sm"
        variant="ghost"
        className="self-start"
        onClick={() => update((b) => void (b.orderBy = [...(b.orderBy ?? []), { expression: blank() }]))}
        data-testid="query-add-order"
      >
        <Plus /> Add order
      </Button>
      {paging ? (
        <>
          <SectionTitle>Paging</SectionTitle>
          <div className="flex flex-wrap gap-4" data-testid="query-paging">
            {(["offset", "limit"] as const).map((bound) => (
              <PagingBound
                key={bound}
                label={PAGING_LABELS[bound]}
                aria={`Paging ${bound}`}
                value={paging.value?.[bound]}
                env={env}
                onChange={(v) =>
                  paging.update((d) => {
                    const next = { ...(d.paging ?? {}) };
                    if (v === undefined) delete next[bound];
                    else next[bound] = v;
                    if (Object.keys(next).length) d.paging = next;
                    else delete d.paging;
                  })
                }
              />
            ))}
          </div>
        </>
      ) : null}
    </div>
  );
}

/** One paging bound: none, a number, or an integer parameter's name. */
function PagingBound({
  label,
  aria,
  value,
  env,
  onChange,
}: {
  label: string;
  aria: string;
  value: string | number | undefined;
  env: QueryEnv;
  onChange: (v: string | number | undefined) => void;
}) {
  const mode = value === undefined ? "none" : typeof value === "number" ? "number" : "param";
  const integers = env.parameters.filter((p) => ["int16", "int32", "int64"].includes(p.type) && !p.collection);
  return (
    <div className="flex items-center gap-1">
      <span className="text-12 text-secondary">{label}</span>
      <select
        aria-label={aria}
        className={`${cellSelect} font-sans`}
        value={mode}
        onChange={(e) =>
          onChange(e.target.value === "none" ? undefined : e.target.value === "number" ? (aria.endsWith("limit") ? 50 : 0) : (integers[0]?.name ?? ""))
        }
      >
        <option value="none">none</option>
        <option value="number">a number</option>
        <option value="param" disabled={!integers.length && mode !== "param"}>
          a parameter
        </option>
      </select>
      {mode === "number" ? (
        <CommitInput
          label={`${aria} number`}
          mono
          className="h-6 w-20 text-12"
          value={String(value)}
          onCommit={(t) => {
            if (/^\d+$/.test(t.trim())) onChange(Number(t.trim()));
          }}
        />
      ) : null}
      {mode === "param" ? (
        <ParamSelect value={String(value ?? "")} env={{ ...env, parameters: integers }} label={`${aria} parameter`} onChange={(p) => onChange(p)} />
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ parameters

const FACETS: TypeFacet[] = ["length", "precision", "scale"];
const FACET_LABELS: Record<TypeFacet, string> = { length: "Len", precision: "Prec", scale: "Scale" };
const NUMERIC = new Set(["int16", "int32", "int64", "decimal", "float", "double"]);

/** A default typed in a cell, as the parameter's type takes it: a number, true or false, else the text; empty clears it. */
export function parameterDefault(type: string, text: string): string | number | boolean | undefined {
  const t = text.trim();
  if (!t) return undefined;
  if (NUMERIC.has(type) && /^-?\d+(\.\d+)?$/.test(t)) return Number(t);
  if (type === "bool" && (t === "true" || t === "false")) return t === "true";
  return text;
}

/** The parameters grid: name (a rename follows every use), type and facets, list, default and description. */
export function ParametersSection({ doc, update }: { doc: QueryDoc; update: (mutate: (d: QueryDoc) => void) => void }) {
  const parameters = doc.parameters ?? [];
  const [problem, setProblem] = useState<string | null>(null);
  const types = useDatabaseTypes(doc.database);
  const setParameter = (i: number, change: (p: QueryParameter) => void) =>
    update((d) => {
      const list = d.parameters ?? [];
      change(list[i]);
    });
  const add = () =>
    update((d) => {
      const names = new Set((d.parameters ?? []).map((p) => p.name));
      let n = (d.parameters ?? []).length + 1;
      while (names.has(`param${n}`)) n++;
      d.parameters = [...(d.parameters ?? []), { name: `param${n}`, type: "string" }];
    });
  return (
    <div className="flex flex-col gap-2" data-testid="query-parameters">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          The values the caller passes, by name: a built-in type or a database type of this database; a list (for in and not in); the default the generated
          method uses when the caller passes none.
        </p>
        <Button size="sm" variant="ghost" onClick={add} data-testid="query-add-parameter">
          <Plus /> Add parameter
        </Button>
      </div>
      <Problem text={problem} />
      {parameters.length ? (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[52rem] table-fixed border-collapse text-12">
            <thead>
              <tr>
                <th scope="col" className={`${head} w-32`}>
                  Name
                </th>
                <th scope="col" className={`${head} w-32`}>
                  Type
                </th>
                {FACETS.map((f) => (
                  <th key={f} scope="col" className={`${head} w-12`}>
                    {FACET_LABELS[f]}
                  </th>
                ))}
                <th scope="col" className={`${head} w-10`}>
                  List
                </th>
                <th scope="col" className={`${head} w-24`}>
                  Default
                </th>
                <th scope="col" className={head}>
                  Description
                </th>
                <th scope="col" className="w-[4.5rem]">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {parameters.map((p, i) => (
                <tr key={i} className="border-t border-default" data-testid={`query-parameter-${p.name}`}>
                  <td className={cell}>
                    <CommitInput
                      label={`Name of parameter ${i + 1}`}
                      mono
                      className="h-6 text-12"
                      value={p.name}
                      onCommit={(v) => {
                        const next = v.trim();
                        const why = nameProblem(
                          next,
                          parameters.filter((_, j) => j !== i).map((x) => x.name),
                          "parameter",
                        );
                        setProblem(why);
                        if (!why) update((d) => void renameParameter(d, p.name, next));
                      }}
                    />
                  </td>
                  <td className={cell}>
                    <TypeSelect
                      label={`Type of parameter ${p.name}`}
                      value={p.type}
                      types={types}
                      allowNative={false}
                      onChange={(v) => v && setParameter(i, (x) => void (x.type = v))}
                    />
                  </td>
                  {FACETS.map((f) => (
                    <td key={f} className={cell}>
                      <CommitInput
                        label={`${f[0].toUpperCase()}${f.slice(1)} of parameter ${p.name}`}
                        mono
                        className="h-6 text-12"
                        value={typeof p[f] === "number" ? String(p[f]) : ""}
                        onCommit={(v) => {
                          const why = facetProblem(f, v);
                          setProblem(why ? `${f[0].toUpperCase()}${f.slice(1)}: ${why}` : null);
                          if (!why) setParameter(i, (x) => void setFacet(x as unknown as Record<string, unknown>, f, v));
                        }}
                      />
                    </td>
                  ))}
                  <td className={cell}>
                    <input
                      type="checkbox"
                      aria-label={`Parameter ${p.name} is a list`}
                      checked={p.collection === true}
                      onChange={(e) =>
                        setParameter(i, (x) => {
                          if (e.target.checked) x.collection = true;
                          else delete x.collection;
                        })
                      }
                    />
                  </td>
                  <td className={cell}>
                    <CommitInput
                      label={`Default of parameter ${p.name}`}
                      mono
                      className="h-6 text-12"
                      value={p.default === undefined ? "" : String(p.default)}
                      placeholder="none"
                      onCommit={(v) =>
                        setParameter(i, (x) => {
                          const value = parameterDefault(x.type, v);
                          if (value === undefined) delete x.default;
                          else x.default = value;
                        })
                      }
                    />
                  </td>
                  <td className={cell}>
                    <CommitInput
                      label={`Description of parameter ${p.name}`}
                      className="h-6 text-12"
                      value={p.description ?? ""}
                      onCommit={(v) =>
                        setParameter(i, (x) => {
                          if (v.trim()) x.description = v.trim();
                          else delete x.description;
                        })
                      }
                    />
                  </td>
                  <td className={`${cell} whitespace-nowrap`}>
                    <MoveButtons
                      noun={`parameter ${p.name}`}
                      first={i === 0}
                      last={i === parameters.length - 1}
                      move={(by) =>
                        update((d) => {
                          const list = [...(d.parameters ?? [])];
                          [list[i], list[i + by]] = [list[i + by], list[i]];
                          d.parameters = list;
                        })
                      }
                    />
                    <Button
                      size="icon-row"
                      variant="ghost"
                      label={`Delete parameter ${p.name}`}
                      onClick={() =>
                        update((d) => {
                          d.parameters = (d.parameters ?? []).filter((_, j) => j !== i);
                          if (!d.parameters.length) delete d.parameters;
                        })
                      }
                    >
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <EmptyState title="No parameters">The query takes no values from its caller.</EmptyState>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ collections

/**
 * The collections: one card each, named (or, for an older query with a result entity, naming the member it fills), with the
 * nested query's sources, select list, filter (the outer aliases in scope for the correlation) and order.
 */
export function CollectionsSection({
  doc,
  update,
  q,
  resolved,
}: {
  doc: QueryDoc;
  update: (mutate: (d: QueryDoc) => void) => void;
  q: QueryContext;
  resolved?: QueryView["collections"];
}) {
  const shape = useEntityShape(doc.entity);
  const collections = doc.collections ?? [];
  const outer = scopeOf(doc, q.view, q.files);
  const add = () =>
    update((d) => {
      const used = new Set((d.collections ?? []).map((c) => c.attribute));
      const target = shape.collections.find((c) => !used.has(c.value));
      const source = target?.entity ? findSource(q.view, target.entity, q.files) : null;
      const alias = defaultAlias(
        source?.name ?? "items",
        outer.map((o) => o.alias),
      );
      const nested: Subquery = { from: { source: source?.value ?? sourceOptions(q.view)[0]?.value ?? "", alias }, select: [] };
      // The correlation: the foreign key between the new source and the parent's, as the nested where.
      const joined = scopeOf(nested, q.view, q.files)[0];
      const fk = joined ? foreignKeyJoins(joined, outer)[0] : undefined;
      if (fk) nested.where = fk.on;
      d.collections = [...(d.collections ?? []), { attribute: target?.value ?? `items${used.size + 1}`, query: nested }];
    });
  return (
    <div className="flex flex-col gap-2" data-testid="query-collections">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          A collection fills a list per result row from a nested query, run once for all the parent rows; its filter ties its rows to the parent with equalities
          between its columns and the parent&apos;s (the parent&apos;s aliases are in scope).
        </p>
        <Button size="sm" variant="ghost" onClick={add} data-testid="query-add-collection">
          <Plus /> Add collection
        </Button>
      </div>
      {collections.length ? null : <EmptyState title="No collections">Each result row is filled from the select list alone.</EmptyState>}
      {collections.map((c, i) => (
        <CollectionCard
          key={i}
          index={i}
          collection={c}
          doc={doc}
          targets={shape.collections}
          q={{ ...q, outer }}
          resolved={resolved?.[i]}
          update={(mutate) => update((d) => mutate(d.collections![i]))}
          remove={() =>
            update((d) => {
              d.collections = (d.collections ?? []).filter((_, j) => j !== i);
              if (!d.collections.length) delete d.collections;
            })
          }
        />
      ))}
    </div>
  );
}

function CollectionCard({
  index,
  collection,
  doc,
  targets,
  q,
  resolved,
  update,
  remove,
}: {
  index: number;
  collection: Collection;
  doc: QueryDoc;
  targets: readonly CollectionTarget[];
  q: QueryContext;
  resolved?: QueryView["collections"][number];
  update: (mutate: (c: Collection) => void) => void;
  remove: () => void;
}) {
  const target = targets.find((t) => t.value === collection.attribute);
  const element = collection.entity ?? target?.entity ?? null;
  const name = resolved?.name ?? target?.label ?? collection.attribute;
  const label = `Collection ${index + 1}`;
  const body = collection.query;
  const bodyUpdate: BodyUpdate = (mutate) => update((c) => mutate(c.query));
  return (
    <section
      className="flex flex-col gap-2 rounded-control border border-default p-2"
      aria-label={`${label}: ${name}`}
      data-testid={`query-collection-${index + 1}`}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-13 font-semibold">{name}</span>
        <label className="flex items-center gap-1 text-12 text-secondary">
          Fills
          {doc.entity ? (
            <select
              aria-label={`${label} fills`}
              className={`${cellSelect} font-sans`}
              value={collection.attribute}
              onChange={(e) => update((c) => void (c.attribute = e.target.value))}
            >
              {target ? null : <option value={collection.attribute}>{collection.attribute} (not in the shape)</option>}
              {targets.map((t) => (
                <option key={t.value} value={t.value}>
                  {t.label}
                </option>
              ))}
            </select>
          ) : (
            <CommitInput
              label={`${label} name`}
              mono
              className="h-6 w-32 text-12"
              value={collection.attribute}
              onCommit={(v) => !nameProblem(v.trim(), [], "collection") && update((c) => void (c.attribute = v.trim()))}
            />
          )}
        </label>
        <span className="flex-1" />
        <Button size="icon-row" variant="ghost" label={`Remove collection ${name}`} onClick={remove}>
          <Trash2 />
        </Button>
      </div>
      <SectionTitle>Sources</SectionTitle>
      <SourcesSection body={body} update={bodyUpdate} q={q} label={label} />
      <SectionTitle>Select</SectionTitle>
      <SelectSection body={body} update={bodyUpdate} q={q} entity={element} label={label} resolved={resolved?.select} />
      <SectionTitle>Filter</SectionTitle>
      <FilterSection body={body} update={bodyUpdate} q={q} label={`${label} where`} />
      <SectionTitle>Group and order</SectionTitle>
      <GroupOrderSection body={body} update={bodyUpdate} q={q} label={label} />
      {resolved?.keys.length ? (
        <p className="text-11 text-secondary" data-testid="query-collection-keys">
          Tied to the parent row by its {resolved.keys.map((k) => (k.hidden ? `hidden column ${k.parentField}` : k.parentField)).join(" and ")}.
        </p>
      ) : null}
    </section>
  );
}
