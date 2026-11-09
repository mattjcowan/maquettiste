// The Storage tab's card for a relation with an end of several types, in one database (several-types-design.md 19.10): the Held in and
// Linked by choices, the pickers they need, the link table's rows and attribute columns. The mapping names ids only, and an entry is
// written once it has a way (an entry with none is MQ4009, which a save refuses), so a choice that names nothing yet is this card's own
// state. The pure side is relationStorage.ts.
import { useState } from "react";
import { useElements } from "@/api/queries";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import type { Binding } from "./fieldMap";
import { ConstantsGrid } from "./StorageTab";
import {
  HELD_IN_LABELS,
  LINKED_BY_LABELS,
  clearEntryWays,
  copyStorageKeys,
  entryAgrees,
  entryKind,
  idColumnChoices,
  keysReferring,
  linkTableComplete,
  linkedByEnds,
  offeredHeldIn,
  offeredLinkedBy,
  pairOfEnds,
  pointerColumnIds,
  primaryKeyColumn,
  readChoice,
  setAttributeColumn,
  setEntryColumns,
  setEntryKey,
  setEntryKeys,
  setHeldIn,
  setLinkTable,
  setMappingConstants,
  setRefersTo,
  setTypeKey,
  sharedTables,
  storageEndOf,
  typeColumnChoices,
  uniqueColumns,
  type EndEntry,
  type HeldIn,
  type LinkedBy,
  type StorageEnd,
  type StorageEndSource,
  type StorageMapping,
  type TableDocWithKeys,
} from "./relationStorage";

export type ChangeMapping = (mutate: (json: StorageMapping) => void) => void;

export interface SeveralCardProps {
  dom: string;
  databaseName: string;
  /** The relation's ends, as its document writes them. */
  ends: StorageEndSource[];
  roleOf: (endId: string) => string;
  mapping: (StorageMapping & { id: string }) | null;
  change: ChangeMapping;
  loading: boolean;
  tableOf: (id: string | null | undefined) => TableDocWithKeys | undefined;
  /** The table an entity is bound to in this database. */
  boundTable: (entity: string) => string | null;
  /** Every table the entity or one of its bases is bound to, for A key to the shared base table. */
  sharedBound: (entity: string) => ReadonlySet<string>;
  entityName: (id: string) => string;
  /** The designed tables of this database that no entity is bound to: the link tables. */
  linkTables: { id: string; name: string }[];
  attributes: { id: string; name: string }[];
}

const none = "";

export function SeveralTypesCard(props: SeveralCardProps) {
  const { dom, databaseName, ends, roleOf, mapping, change, loading, tableOf, boundTable, sharedBound, entityName, linkTables, attributes } = props;
  const models = ends.map(storageEndOf);
  const [heldDraft, setHeldDraft] = useState<HeldIn | null>(null);
  const [linkedDraft, setLinkedDraft] = useState<Record<string, LinkedBy>>({});
  // A link table is accepted only with an entry for each end, so an edit that leaves it short is held here and written when it is whole.
  const [staged, setStaged] = useState<StorageMapping | null>(null);
  const shown: (StorageMapping & { id: string }) | null = staged ? { ...staged, id: mapping?.id ?? "" } : mapping;
  // A draft that agrees with what is written wins over how the file alone reads, so a half-written choice keeps its pickers.
  const choice = readChoice(shown, models, heldDraft);
  const asWritten = choice.heldIn === "as-written";
  const heldIn: HeldIn | null = choice.heldIn && choice.heldIn !== "as-written" ? choice.heldIn : null;
  const pair = pairOfEnds(models);
  const offered = offeredHeldIn(models);
  const entryOf = (end: StorageEnd): EndEntry | undefined => shown?.ends?.find((e) => e.end === end.id);
  const linkedOf = (end: StorageEnd): LinkedBy | "" => {
    const draft = linkedDraft[end.id];
    if (draft && entryAgrees(draft, entryOf(end))) return draft;
    return choice.linkedBy[end.id] || "";
  };
  const junctionDocs = useElements(shown?.junctionTable && !tableOf(shown.junctionTable) ? [shown.junctionTable] : []);
  const junction = tableOf(shown?.junctionTable) ?? (junctionDocs.byId.get(shown?.junctionTable ?? "")?.json as TableDocWithKeys | undefined);
  const needsLinkTables = heldIn === "table-per-type";
  const linkDocs = useElements(needsLinkTables ? linkTables.map((t) => t.id) : []);
  const heldSelect = asWritten ? "as-written" : (heldIn ?? none);

  /** The edit the pickers make: written at once, or held while a link table lacks an entry for an end. */
  const edit: ChangeMapping = (mutate) => {
    if (heldIn === "link-table" || staged) {
      const next = structuredClone((shown ?? {}) as StorageMapping & { id?: string });
      delete next.id;
      mutate(next);
      const bare = !next.junctionTable && !next.ends?.length;
      if (!linkTableComplete(next, models) && !bare) {
        setStaged(next);
        return;
      }
      if (staged) {
        setStaged(null);
        change((j) => copyStorageKeys(j as unknown as Record<string, unknown>, next));
        return;
      }
    }
    change(mutate);
  };

  const pickHeld = (value: string) => {
    const next = (value || null) as HeldIn | null;
    setHeldDraft(next);
    setLinkedDraft({});
    setStaged(null);
    // A choice that names nothing yet writes nothing, except Not in this database and a change on a mapping that exists.
    if (next && (next === "none" || mapping)) change((j) => setHeldIn(j, next, models));
  };
  const pickLinked = (end: StorageEnd, value: string) => {
    const next = value as LinkedBy;
    setLinkedDraft((d) => ({ ...d, [end.id]: next }));
    if (entryOf(end)) edit((j) => clearEntryWays(j, end.id));
  };

  return (
    <div className="flex flex-col gap-1.5" data-testid="relation-storage-several">
      <div className="flex flex-wrap items-end gap-2">
        <Field label="Held in" htmlFor={`${dom}-held`}>
          <Select
            id={`${dom}-held`}
            aria-label={`Held in, in ${databaseName}`}
            className="w-56"
            data-testid="relation-held-in"
            value={heldSelect}
            disabled={loading}
            onChange={(e) => pickHeld(e.target.value)}
          >
            <option value={none}>(not chosen)</option>
            {asWritten ? (
              <option value="as-written" disabled>
                As written
              </option>
            ) : null}
            {offered.map((h) => (
              <option key={h} value={h}>
                {HELD_IN_LABELS[h]}
              </option>
            ))}
          </Select>
        </Field>
        {heldIn === "link-table" ? (
          <Field label="Link table" htmlFor={`${dom}-link`}>
            <Select
              id={`${dom}-link`}
              aria-label={`Link table in ${databaseName}`}
              className="w-56"
              data-testid="relation-link-table"
              value={shown?.junctionTable ?? none}
              disabled={loading}
              onChange={(e) => {
                const table = e.target.value || null;
                edit((j) => setLinkTable(j, table));
              }}
            >
              <option value={none}>(none named)</option>
              {linkTables.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </Select>
          </Field>
        ) : null}
      </div>
      {staged ? (
        <p className="text-12 text-secondary" data-testid="relation-storage-held">
          Not saved yet: a link table is saved once each end says how it is pointed at.
        </p>
      ) : null}
      {asWritten && mapping ? (
        <p className="text-12 text-secondary" data-testid="relation-storage-as-written">
          This mapping matches none of the choices; each end's entry is shown as written. Choose a Held in to start again.
        </p>
      ) : null}
      {(asWritten ? models : linkedByEnds(heldIn, models)).map((end) => {
        const holder = holderOf();
        const kinds = asWritten ? (Object.keys(LINKED_BY_LABELS) as LinkedBy[]) : offeredLinkedBy(heldIn, end);
        const written = entryOf(end) ? entryKind(entryOf(end)!, end) : "";
        const current = asWritten ? (written === "keys" || written === "mixed" ? "" : written) || linkedOf(end) : linkedOf(end);
        const state = holder ? "ready" : heldIn === "link-table" && !asWritten ? "no-table" : "no-holder";
        return (
          <div key={end.id} className="flex flex-col gap-1 rounded-control border border-input p-1.5" data-testid="relation-storage-end">
            <div className="flex flex-wrap items-end gap-2">
              <Field label={`Linked by: ${roleOf(end.id)}`} htmlFor={`${dom}-linked-${end.id}`}>
                <Select
                  id={`${dom}-linked-${end.id}`}
                  aria-label={`Linked by, ${roleOf(end.id)}`}
                  className="w-64"
                  data-testid="relation-linked-by"
                  value={current}
                  disabled={loading || kinds.length === 1}
                  onChange={(e) => pickLinked(end, e.target.value)}
                >
                  {kinds.length === 1 ? null : <option value={none}>(not chosen)</option>}
                  {kinds.map((k) => (
                    <option key={k} value={k}>
                      {LINKED_BY_LABELS[k]}
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
            {state === "no-table" ? <p className="text-12 text-secondary">Name the link table first.</p> : null}
            {state === "no-holder" ? (
              <p className="text-12 text-secondary">The table that holds the link is not known yet: bind the other end to a table.</p>
            ) : null}
            {state === "ready" && (current || kinds.length === 1) ? (
              <EntryPickers
                dom={`${dom}-${end.id}`}
                kind={current || kinds[0]}
                end={end}
                entry={entryOf(end)}
                holder={holder}
                change={edit}
                tableOf={tableOf}
                boundTable={boundTable}
                sharedBound={sharedBound}
                entityName={entityName}
                loading={loading}
                otherTable={otherEndTable(end)}
              />
            ) : null}
          </div>
        );
      })}
      {heldIn === "type-tables" && pair?.a && pair.b ? (
        <TypeTables
          dom={dom}
          a={pair.a}
          b={pair.b}
          entry={entryOf(pair.a)}
          change={edit}
          tableOf={tableOf}
          boundTable={boundTable}
          entityName={entityName}
          loading={loading}
        />
      ) : null}
      {heldIn === "table-per-type" && pair?.a && pair.b ? (
        <TablePerType
          dom={dom}
          a={pair.a}
          b={pair.b}
          mapping={shown}
          change={edit}
          linkTables={linkTables.map((t) => ({ ...t, doc: linkDocs.byId.get(t.id)?.json as TableDocWithKeys | undefined }))}
          tableOf={tableOf}
          boundTable={boundTable}
          entityName={entityName}
          loading={loading}
        />
      ) : null}
      {junction && heldIn !== "other-table" && shown ? (
        <LinkTableExtras dom={dom} junction={junction} mapping={shown} attributes={attributes} change={edit} loading={loading} />
      ) : null}
      {heldIn === "other-table" && shown && attributes.length && pair?.a ? (
        <AttributeColumns
          dom={dom}
          columns={endTableColumns()}
          mapping={shown}
          attributes={attributes}
          change={edit}
          loading={loading}
          title="Attribute columns, in the other end's table"
        />
      ) : null}
    </div>
  );

  /** The columns of the one-type end's table that an attribute may use: not the ones that point, nor the primary key. */
  function endTableColumns() {
    const table = pair?.a ? tableOf(boundTable(pair.a.entities[0] ?? "")) : undefined;
    const pointers = pointerColumnIds(table, shown ?? {});
    return (table?.columns ?? []).filter((c) => !pointers.has(c.id) && !table?.primaryKey?.columns.includes(c.id));
  }

  /** The table whose rows hold the link of this end's entry: the link table, or the one-type end's table under The other end's table. */
  function holderOf(): TableDocWithKeys | undefined {
    if (heldIn === "link-table" || (asWritten && shown?.junctionTable)) return junction;
    const a = pair?.a;
    return a ? tableOf(boundTable(a.entities[0] ?? "")) : undefined;
  }

  /** The table of the other end's one entity, which a key to this end's entity points at (a key on a one-type end). */
  function otherEndTable(end: StorageEnd): string | null {
    return end.entities.length === 1 ? boundTable(end.entities[0]) : null;
  }
}

// ------------------------------------------------------------------ the pickers of one end's entry

interface PickerProps {
  dom: string;
  kind: LinkedBy;
  end: StorageEnd;
  entry: EndEntry | undefined;
  holder: TableDocWithKeys | undefined;
  change: ChangeMapping;
  tableOf: SeveralCardProps["tableOf"];
  boundTable: SeveralCardProps["boundTable"];
  sharedBound: SeveralCardProps["sharedBound"];
  entityName: SeveralCardProps["entityName"];
  loading: boolean;
  otherTable: string | null;
}

/** A column a mapping names that the table doesn't have: said as missing, by name when the name is known. */
export const missingColumnLabel = (name: string | undefined) => (name ? `${name} (missing)` : "(missing column)");

const keyLabel = (
  holder: TableDocWithKeys,
  k: { id: string; name?: string | undefined; columns: string[]; referencesTable: string },
  tableName: (id: string) => string,
) => `${k.name ?? k.id} (${k.columns.map((c) => holder.columns?.find((x) => x.id === c)?.name ?? c).join(", ")} → ${tableName(k.referencesTable)})`;

function EntryPickers(p: PickerProps) {
  const { dom, kind, end, entry, holder, change, tableOf, boundTable, sharedBound, entityName, loading, otherTable } = p;
  const refTables = useElements(holder?.foreignKeys?.map((k) => k.referencesTable).filter((t) => !t.includes("@")) ?? []);
  const tableName = (id: string) => (refTables.byId.get(id)?.json as { name?: string } | undefined)?.name ?? tableOf(id)?.name ?? id;
  if (!holder) return null;
  const keyOptions = (keys: ReturnType<typeof keysReferring>) =>
    keys.map((k) => (
      <option key={k.id} value={k.id}>
        {keyLabel(holder, k, tableName)}
      </option>
    ));
  const orderOf = end.entities;
  const refersTo = (entity: string) => entry?.types?.find((t) => t.entity === entity)?.referencesColumn ?? none;
  const referencedColumn = () => {
    const first = end.entities[0];
    const table = tableOf(boundTable(first ?? ""));
    const named = first ? uniqueColumns(table).find((c) => c.id === refersTo(first)) : undefined;
    return named ?? primaryKeyColumn(table);
  };

  if (kind === "key") {
    const keys = keysReferring(holder, new Set(otherTable ? [otherTable] : []));
    return (
      <Field label="Key" htmlFor={`${dom}-key`}>
        <Select
          id={`${dom}-key`}
          aria-label="Key to the end's table"
          className="w-96"
          data-testid="relation-entry-key"
          value={entry?.foreignKey ?? none}
          disabled={loading}
          onChange={(e) => {
            const key = e.target.value || null;
            change((j) => setEntryKey(j, end.id, key));
          }}
        >
          <option value={none}>(none named)</option>
          {keyOptions(keys)}
        </Select>
      </Field>
    );
  }
  if (kind === "shared-key") {
    const shared = sharedTables(end.entities.map((e) => sharedBound(e)));
    const keys = shared.size ? keysReferring(holder, shared) : (holder.foreignKeys ?? []);
    return (
      <Field
        label="Key to the shared base table"
        htmlFor={`${dom}-shared`}
        hint={shared.size ? undefined : "No base table every type shares is bound: every key of the table is offered."}
      >
        <Select
          id={`${dom}-shared`}
          aria-label="Key to the shared base table"
          className="w-96"
          data-testid="relation-entry-key"
          value={entry?.foreignKey ?? none}
          disabled={loading}
          onChange={(e) => {
            const key = e.target.value || null;
            change((j) => setEntryKey(j, end.id, key));
          }}
        >
          <option value={none}>(none named)</option>
          {keyOptions(keys)}
        </Select>
      </Field>
    );
  }
  if (kind === "column-per-type")
    return (
      <div className="flex flex-col gap-1">
        {end.entities.map((entity) => {
          const keys = keysReferring(holder, new Set([boundTable(entity) ?? ""]));
          const value = entry?.types?.find((t) => t.entity === entity)?.foreignKey ?? none;
          return (
            <Field key={entity} label={`Key for ${entityName(entity)}`} htmlFor={`${dom}-key-${entity}`}>
              <Select
                id={`${dom}-key-${entity}`}
                className="w-96"
                data-testid="relation-type-key"
                value={value}
                disabled={loading}
                onChange={(e) => {
                  const key = e.target.value || null;
                  change((j) => setTypeKey(j, end.id, orderOf, entity, key));
                }}
              >
                <option value={none}>(none named)</option>
                {keyOptions(keys)}
              </Select>
            </Field>
          );
        })}
      </div>
    );
  // A type and an id, or an id unique across types.
  const withType = kind === "type-and-id";
  const idChosen = !!entry?.idColumn;
  const idColumns = idColumnChoices(holder, referencedColumn(), entry?.typeColumn);
  const columnName = (id: string | undefined) => holder.columns?.find((c) => c.id === id)?.name;
  return (
    <div className="flex flex-col gap-1">
      <div className="flex flex-wrap items-end gap-2">
        <Field label="Id column" htmlFor={`${dom}-id`}>
          <Select
            id={`${dom}-id`}
            aria-label="Id column"
            className="w-56"
            data-testid="relation-id-column"
            value={entry?.idColumn ?? none}
            disabled={loading}
            onChange={(e) => {
              const column = e.target.value;
              change((j) => (column ? setEntryColumns(j, end.id, { idColumn: column }) : clearEntryWays(j, end.id)));
            }}
          >
            <option value={none}>(none named)</option>
            {entry?.idColumn && !idColumns.some((c) => c.id === entry.idColumn) ? (
              <option value={entry.idColumn}>{missingColumnLabel(columnName(entry.idColumn))}</option>
            ) : null}
            {idColumns.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name ?? c.id}
              </option>
            ))}
          </Select>
        </Field>
        {withType ? (
          <Field label="Type column" htmlFor={`${dom}-type`} hint={idChosen ? undefined : "Name the id column first."}>
            <Select
              id={`${dom}-type`}
              aria-label="Type column"
              className="w-56"
              data-testid="relation-type-column"
              value={entry?.typeColumn ?? none}
              disabled={loading || !idChosen}
              onChange={(e) => {
                const column = e.target.value || null;
                change((j) => setEntryColumns(j, end.id, { typeColumn: column }));
              }}
            >
              <option value={none}>(none named)</option>
              {typeColumnChoices(holder, entry?.idColumn).map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name ?? c.id}
                </option>
              ))}
            </Select>
          </Field>
        ) : null}
      </div>
      {idChosen
        ? end.entities.map((entity) => {
            if (!boundTable(entity)) return null;
            const table = tableOf(boundTable(entity));
            const unique = uniqueColumns(table);
            const primary = primaryKeyColumn(table);
            return (
              <Field
                key={entity}
                label={`Refers to, in ${entityName(entity)}`}
                htmlFor={`${dom}-refers-${entity}`}
                hint={primary || refersTo(entity) ? undefined : "This table's primary key is not one column: name the unique column the id refers to."}
              >
                <Select
                  id={`${dom}-refers-${entity}`}
                  className="w-56"
                  data-testid="relation-refers-to"
                  value={refersTo(entity)}
                  disabled={loading || !table}
                  onChange={(e) => {
                    const column = e.target.value || null;
                    change((j) => setRefersTo(j, end.id, orderOf, entity, column));
                  }}
                >
                  <option value={none}>{primary ? `(primary key: ${primary.name ?? primary.id})` : "(none named)"}</option>
                  {unique
                    .filter((c) => c.id !== primary?.id)
                    .map((c) => (
                      <option key={c.id} value={c.id}>
                        {c.name ?? c.id}
                      </option>
                    ))}
                </Select>
              </Field>
            );
          })
        : null}
    </div>
  );
}

// ------------------------------------------------------------------ Each type's table, and A table per type

/** Each type's table (B's) holds a key to A: one key per type, each in that type's table. */
function TypeTables(props: {
  dom: string;
  a: StorageEnd;
  b: StorageEnd;
  entry: EndEntry | undefined;
  change: ChangeMapping;
  tableOf: SeveralCardProps["tableOf"];
  boundTable: SeveralCardProps["boundTable"];
  entityName: SeveralCardProps["entityName"];
  loading: boolean;
}) {
  const { dom, a, b, entry, change, tableOf, boundTable, entityName, loading } = props;
  const aTable = boundTable(a.entities[0] ?? "");
  const chosen = (entity: string) => {
    const table = tableOf(boundTable(entity));
    return (entry?.foreignKeys ?? []).find((k) => table?.foreignKeys?.some((x) => x.id === k)) ?? none;
  };
  const set = (entity: string, key: string) => {
    const keys = b.entities.map((e) => (e === entity ? key : chosen(e))).filter(Boolean);
    change((j) => setEntryKeys(j, a.id, keys));
  };
  return (
    <div className="flex flex-col gap-1 rounded-control border border-input p-1.5" data-testid="relation-storage-type-tables">
      {b.entities.map((entity) => {
        const table = tableOf(boundTable(entity));
        const keys = keysReferring(table, new Set(aTable ? [aTable] : []));
        return (
          <Field
            key={entity}
            label={`Key in ${table?.name ?? entityName(entity)}`}
            htmlFor={`${dom}-tt-${entity}`}
            hint={
              table ? (keys.length ? undefined : "No key in this table refers to the other end's table: add one first.") : "Bind this type to a table first."
            }
          >
            <Select
              id={`${dom}-tt-${entity}`}
              className="w-96"
              data-testid="relation-type-table-key"
              value={chosen(entity)}
              disabled={loading || !table}
              onChange={(e) => set(entity, e.target.value)}
            >
              <option value={none}>(none named)</option>
              {keys.map((k) => (
                <option key={k.id} value={k.id}>
                  {k.name ?? k.id}
                </option>
              ))}
            </Select>
          </Field>
        );
      })}
    </div>
  );
}

/** A table per type: a link table for each type of B, with a key to A and a key to that type. */
function TablePerType(props: {
  dom: string;
  a: StorageEnd;
  b: StorageEnd;
  mapping: StorageMapping | null;
  change: ChangeMapping;
  linkTables: { id: string; name: string; doc: TableDocWithKeys | undefined }[];
  tableOf: SeveralCardProps["tableOf"];
  boundTable: SeveralCardProps["boundTable"];
  entityName: SeveralCardProps["entityName"];
  loading: boolean;
}) {
  const { dom, a, b, mapping, change, linkTables, boundTable, entityName, loading } = props;
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const aTable = boundTable(a.entities[0] ?? "");
  const typeKey = (entity: string) => mapping?.ends?.find((e) => e.end === b.id)?.types?.find((t) => t.entity === entity)?.foreignKey ?? none;
  // The table of a type is the one that owns its key, else the one picked here.
  const tableFor = (entity: string) => {
    const key = typeKey(entity);
    return linkTables.find((t) => (key && t.doc?.foreignKeys?.some((k) => k.id === key)) || t.id === drafts[entity]);
  };
  const aKeyOf = (entity: string) => {
    const t = tableFor(entity);
    return (mapping?.ends?.find((e) => e.end === a.id)?.foreignKeys ?? []).find((k) => t?.doc?.foreignKeys?.some((x) => x.id === k)) ?? none;
  };
  const setAKey = (entity: string, key: string) =>
    change((j) => setEntryKeys(j, a.id, b.entities.map((e) => (e === entity ? key : aKeyOf(e))).filter(Boolean)));
  return (
    <div className="flex flex-col gap-1 rounded-control border border-input p-1.5" data-testid="relation-storage-table-per-type">
      {b.entities.map((entity) => {
        const table = tableFor(entity);
        const toType = keysReferring(table?.doc, new Set([boundTable(entity) ?? ""]));
        const toA = keysReferring(table?.doc, new Set(aTable ? [aTable] : []));
        return (
          <div key={entity} className="flex flex-wrap items-end gap-2">
            <Field label={`Table for ${entityName(entity)}`} htmlFor={`${dom}-tpt-${entity}`}>
              <Select
                id={`${dom}-tpt-${entity}`}
                className="w-48"
                data-testid="relation-per-type-table"
                value={table?.id ?? none}
                disabled={loading}
                onChange={(e) => setDrafts((d) => ({ ...d, [entity]: e.target.value }))}
              >
                <option value={none}>(none named)</option>
                {linkTables.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Key to the other end" htmlFor={`${dom}-tpa-${entity}`}>
              <Select
                id={`${dom}-tpa-${entity}`}
                className="w-56"
                value={aKeyOf(entity)}
                disabled={loading || !table}
                onChange={(e) => setAKey(entity, e.target.value)}
              >
                <option value={none}>(none named)</option>
                {toA.map((k) => (
                  <option key={k.id} value={k.id}>
                    {k.name ?? k.id}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label={`Key to ${entityName(entity)}`} htmlFor={`${dom}-tpk-${entity}`}>
              <Select
                id={`${dom}-tpk-${entity}`}
                className="w-56"
                value={typeKey(entity)}
                disabled={loading || !table}
                onChange={(e) => {
                  const key = e.target.value || null;
                  change((j) => setTypeKey(j, b.id, b.entities, entity, key));
                }}
              >
                <option value={none}>(none named)</option>
                {toType.map((k) => (
                  <option key={k.id} value={k.id}>
                    {k.name ?? k.id}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
        );
      })}
    </div>
  );
}

// ------------------------------------------------------------------ rows and attribute columns of a link table

/**
 * "Rows of this relation" (constants) and a column per relation attribute, for a mapping with a designed link table. Offered on every
 * relation mapping with one, a one-type relation included.
 */
export function LinkTableExtras({
  dom,
  junction,
  mapping,
  attributes,
  change,
  loading,
}: {
  dom: string;
  junction: TableDocWithKeys;
  mapping: StorageMapping;
  attributes: { id: string; name: string }[];
  change: ChangeMapping;
  loading: boolean;
}) {
  const pointers = pointerColumnIds(junction, mapping);
  const primary = new Set(junction.primaryKey?.columns ?? []);
  const columns = (junction.columns ?? []).filter((c) => !pointers.has(c.id));
  const sourceColumns = columns.map((c) => ({
    key: c.id,
    name: c.name ?? c.id,
    type: typeof c.type === "string" ? c.type : null,
    nullable: true,
    identity: false,
    computed: false,
    hasDefault: false,
    isPrimaryKey: primary.has(c.id) || primary.has(c.name ?? ""),
  }));
  const pseudo: Binding = {
    id: "",
    database: "",
    source: "",
    constants: (mapping.constants ?? []).map((c) => ({ column: c.column, value: c.value ?? null })),
    fields: [],
  };
  return (
    <div className="flex flex-col gap-1.5" data-testid="relation-storage-link-extras">
      <ConstantsGrid
        dom={`${dom}-rows`}
        binding={pseudo}
        columns={sourceColumns}
        title="Rows of this relation"
        emptyText="None: every row of the link table is a link of this relation."
        skipPrimaryKey
        onEdit={(mutate) => {
          const next: Binding = { ...pseudo, constants: [...(pseudo.constants ?? [])] };
          mutate(next);
          change((j) => setMappingConstants(j, next.constants ?? []));
        }}
      />
      <AttributeColumns dom={dom} columns={columns} mapping={mapping} attributes={attributes} change={change} loading={loading} title="Attribute columns" />
    </div>
  );
}

/** A column for each relation attribute, among the columns given (those not used as a pointer or a constant). */
export function AttributeColumns({
  dom,
  columns,
  mapping,
  attributes,
  change,
  loading,
  title,
}: {
  dom: string;
  columns: { id: string; name?: string | undefined }[];
  mapping: StorageMapping;
  attributes: { id: string; name: string }[];
  change: ChangeMapping;
  loading: boolean;
  title: string;
}) {
  const used = new Set([...(mapping.constants ?? []).map((c) => c.column), ...(mapping.attributes ?? []).map((a) => a.column ?? "")]);
  if (!attributes.length) return null;
  return (
    <section className="flex flex-col gap-1" data-testid="relation-storage-attribute-columns">
      <SectionTitle>{title}</SectionTitle>
      {attributes.map((a) => {
        const value = mapping.attributes?.find((x) => x.attribute === a.id)?.column ?? none;
        const known = columns.some((c) => c.id === value);
        return (
          <Field key={a.id} label={`Column for ${a.name}`} htmlFor={`${dom}-attr-${a.id}`}>
            <Select
              id={`${dom}-attr-${a.id}`}
              className="w-56"
              data-testid="relation-attribute-column"
              value={value}
              disabled={loading}
              onChange={(e) => {
                const column = e.target.value || null;
                change((j) => setAttributeColumn(j, a.id, column));
              }}
            >
              <option value={none}>(none named)</option>
              {value && !known ? <option value={value}>{missingColumnLabel(undefined)}</option> : null}
              {columns
                .filter((c) => !used.has(c.id) || c.id === value)
                .map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name ?? c.id}
                  </option>
                ))}
            </Select>
          </Field>
        );
      })}
    </section>
  );
}
