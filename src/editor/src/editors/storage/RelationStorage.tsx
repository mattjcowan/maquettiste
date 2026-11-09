// The relationship editor's Storage tab (erratum E43): per database, when both ends' entities are bound to tables, the foreign
// key that realizes the relationship (or, for a many-to-many, the junction table and the key of it for each end), written to
// the relation's mapping in that database; otherwise "Bind both ends to a table to name the foreign key", with the shape an
// older mapping names shown read only (no new mapping of the older format is made from here). Each pick is one save and one
// undo step. The pickers wait for the relation's mapping documents, so a pick never creates a
// second mapping for a database that has one; the foreign key picker offers only the keys the engine accepts (held by the
// dependent end's table).
import { useRef } from "react";
import { CircleAlert, TriangleAlert } from "lucide-react";
import { useElements, useIndex, useValidation } from "@/api/queries";
import type { RelationDoc } from "@/api/types";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { newId } from "@/lib/ids";
import { indexLookup } from "@/model/index";
import { ElementLink } from "@/editors/EntityEditor";
import type { RelationMappingLike, RelationShape } from "@/editors/inheritance";
import { useElementEdits } from "@/editors/mappingEdit";
import {
  attributeTakesColumn,
  boundTableOf,
  dependentEnd,
  foreignKeyChoices,
  reverseForeignKeys,
  setEndForeignKey,
  setJunctionTable,
  setRelationForeignKey,
  type TableDocLike,
} from "./relationStorage";
import { databasesOf } from "./useStorage";
import { docEndEntities, docEndHasSeveralTypes, docEndSingleEntity } from "@/model/endTypes";
import { LinkTableExtras, SeveralTypesCard } from "./RelationStorageSeveral";
import { DatabaseRunLists } from "./RelationStorageRun";
import { coveredTargets, type StorageEndSource, type StorageMapping, type TableDocWithKeys } from "./relationStorage";

type Json = Record<string, unknown>;

const SHAPE_LABELS: Record<RelationShape, string> = {
  "foreign-key": "Foreign key",
  junction: "Junction table",
  promoted: "Promoted to an entity",
};

type RelationMappingDoc = RelationMappingLike & StorageMapping & { foreignKey?: string; junctionTable?: string; ends?: { end: string; foreignKey?: string }[] };

export function RelationStorageTab({ id, json }: { id: string; json: RelationDoc }) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const databases = databasesOf(index.data);
  const ends = json.ends ?? [];
  // A relation's mapping has no entity in the index: read those few documents and keep the ones for this relation.
  const candidates = lookup.ofKind("mapping").filter((m) => !m.entity);
  const mappingDocs = useElements(candidates.map((m) => m.id));
  const mappings = candidates
    .map((m): RelationMappingDoc => ({
      ...((mappingDocs.byId.get(m.id)?.json as RelationMappingDoc | undefined) ?? { id: m.id, database: m.database }),
      id: m.id,
    }))
    .filter((m) => m.relation === id);
  // An end with several types lists entities; a one-type end names one. Every listed entity's document gives its bound table, and the
  // bases of those entities give the tables a key to a shared base table may lead to (several-types-design.md 19.10).
  const endEntity = (e: (typeof ends)[number]) => docEndSingleEntity(e) ?? "";
  const hasSeveralTypes = ends.some(docEndHasSeveralTypes);
  const severalMode = hasSeveralTypes && ends.length === 2;
  // The per-type pickers cover every target: the listed entities, the carriers of stereotype entries and what is derived from each.
  // An any-entity end names no list (a key per type is refused there), so it doesn't pull every entity in.
  const summaryEnds = lookup.byId.get(id)?.ends ?? [];
  const entityRows = hasSeveralTypes && ends.length === 2 ? lookup.ofKind("entity") : [];
  const covered = ends.map((e, i) =>
    hasSeveralTypes && ends.length === 2 ? coveredTargets(docEndEntities(e), summaryEnds[i]?.covers ?? [], false, entityRows) : docEndEntities(e),
  );
  const listed = [...new Set(covered.flat())];
  const withBases = new Set(listed);
  if (severalMode)
    for (const entity of listed)
      for (let at = lookup.byId.get(entity)?.base, guard = 0; at && !withBases.has(at) && guard < 64; at = lookup.byId.get(at)?.base, guard++)
        withBases.add(at);
  const wantedEntities = severalMode ? [...withBases] : [...new Set(ends.map(endEntity).filter(Boolean))];
  const entityDocs = useElements(wantedEntities);
  // An entity is bound to a table by a binding on its document, or by an entity mapping file with a `table` (the engine reads both; a binding wins).
  const entityMapRows = lookup.ofKind("mapping").filter((m) => !!m.entity && wantedEntities.includes(m.entity));
  const entityMapDocs = useElements(entityMapRows.map((m) => m.id));
  const mappedTable = (entity: string, database: string): string | null => {
    for (const row of entityMapRows) {
      if (row.entity !== entity || row.database !== database) continue;
      const doc = entityMapDocs.byId.get(row.id)?.json as Json | undefined;
      if (doc && doc.ignore !== true && typeof doc.table === "string") return doc.table;
    }
    return null;
  };
  const boundIn = (entity: string, database: string) =>
    boundTableOf(entityDocs.byId.get(entity)?.json as Json | undefined, database) ?? mappedTable(entity, database);
  const bound = databases.map((d) => ends.map((e) => boundIn(endEntity(e), d.id)));
  const severalTables = severalMode ? databases.flatMap((d) => [...withBases].map((en) => boundIn(en, d.id))) : [];
  const junctions = mappings.map((m) => m.junctionTable).filter((t): t is string => !!t);
  const tableDocs = useElements([...new Set([...bound.flat(), ...severalTables].filter((t): t is string => !!t && !t.includes("@")).concat(junctions))]);
  const tableOf = (tid: string | null | undefined) => (tid ? (tableDocs.byId.get(tid)?.json as TableDocWithKeys | undefined) : undefined);
  // A relation attribute takes one column unless it is a collection or typed by a value object (an enum or a reference type is one column).
  const columnAttributes = (json.attributes ?? [])
    .filter((a) => attributeTakesColumn(a as { type?: unknown; collection?: boolean }, (ref) => lookup.byId.get(ref)?.kind))
    .map((a) => ({ id: a.id, name: a.name }));
  const validation = useValidation();
  const edits = useElementEdits();
  const relationName = json.name;
  const manyToMany = ends.length === 2 && ends.every((e) => (e.max ?? "*") === "*");
  // Until the mapping documents are read, which database has a mapping is not known: no pick until then (no duplicate mapping).
  const mappingsLoading = mappingDocs.pending;
  const creating = useRef(new Set<string>());
  return (
    <div className="flex flex-col gap-2" data-testid="relation-storage">
      {databases.length ? null : <p className="text-12 text-secondary">No database yet.</p>}
      {databases.map((d, di) => {
        const mapping = mappings.find((m) => m.database === d.id) ?? null;
        const change = (mutate: (j: Json) => void) => {
          if (mappingsLoading) return;
          if (mapping) void edits.update(mapping.id, mutate);
          else if (!creating.current.has(d.id)) {
            const created: Json = { kind: "mapping", id: newId(), name: `${relationName} in ${d.name}`, database: d.id, relation: id };
            mutate(created);
            creating.current.add(d.id);
            void edits.create(created, `Store ${relationName} in ${d.name}`).finally(() => creating.current.delete(d.id));
          }
        };
        const dependent = dependentEnd(ends, mapping?.foreignKeyEnd ?? null);
        const [a, b] = bound[di] ?? [];
        const bothBound = ends.length === 2 && !!a && !!b;
        // The relation mapping's own findings, and the relationship's storage rules (MQ4011: bound ends, no key named) in this database.
        const endIds = new Set(ends.map((e) => e.id));
        const diagnostics = validation.data?.diagnostics ?? [];
        const problems = diagnostics.filter(
          (x) =>
            x.severity !== "info" &&
            x.rule !== "MQ4120" &&
            ((!!mapping && x.elementId === mapping.id) ||
              (x.elementId === id && x.rule === "MQ4011" && x.message.includes(d.name)) ||
              (x.rule === "MQ4117" && (x.elementId === id || endIds.has(x.elementId ?? "")) && x.message.includes(`'${d.name}'`))),
        );
        // MQ4115 (info): nothing is stored in this database for a relation with an end of several types.
        const nothingStored = diagnostics.some((x) => x.rule === "MQ4115" && endIds.has(x.elementId ?? "") && x.message.includes(`database '${d.name}'`));
        const summary = mapping ? lookup.byId.get(mapping.id) : undefined;
        return (
          <section key={d.id} className="flex flex-col gap-1" data-testid="relation-storage-database" data-database={d.name}>
            <SectionTitle>{d.name}</SectionTitle>
            {severalMode ? (
              <SeveralTypesCard
                dom={`${id}-${d.id}`}
                databaseName={d.name}
                ends={(ends as StorageEndSource[]).map((e, i) => ({
                  ...e,
                  // A derived or carrier target is offered where the database has a table for it; a listed one always (as before).
                  covered: covered[i].filter((en) => docEndEntities(ends[i]).includes(en) || boundIn(en, d.id)),
                }))}
                roleOf={(endId) => ends.find((e) => e.id === endId)?.role ?? endId}
                mapping={mapping}
                change={(mutate) => change((j) => mutate(j as unknown as StorageMapping))}
                loading={mappingsLoading || tableDocs.pending || entityDocs.pending || entityMapDocs.pending}
                tableOf={tableOf}
                boundTable={(entity) => boundIn(entity, d.id)}
                sharedBound={(entity) => {
                  const out = new Set<string>();
                  for (let at: string | undefined = entity, guard = 0; at && guard < 64; at = lookup.byId.get(at)?.base, guard++) {
                    const t = boundIn(at, d.id);
                    if (t) out.add(t);
                  }
                  return out;
                }}
                entityName={(entity) => lookup.nameOf(entity) ?? entity}
                linkTables={lookup
                  .ofKind("table")
                  .filter((t) => t.database === d.id && !t.entity)
                  .map((t) => ({ id: t.id, name: t.name }))}
                attributes={columnAttributes}
              />
            ) : bothBound && !manyToMany ? (
              <ForeignKeyPicker
                dom={`${id}-${d.id}`}
                databaseName={d.name}
                choices={foreignKeyChoices(tableOf(a), tableOf(b), dependent)}
                reversed={reverseForeignKeys(tableOf(a), tableOf(b), dependent)}
                dependentTable={dependent ? (tableOf(dependent.index === 0 ? a : b)?.name ?? null) : null}
                value={mapping?.foreignKey ?? ""}
                loading={tableDocs.pending || mappingsLoading}
                onPick={(key) => {
                  const choice = foreignKeyChoices(tableOf(a), tableOf(b), dependent).find((c) => c.key.id === key);
                  // A one-to-one tie: the end whose table holds the picked key is the dependent one.
                  const end = dependent?.tie && choice ? ends[choice.end].id : undefined;
                  change((j) => setRelationForeignKey(j, key, end));
                }}
              />
            ) : bothBound && manyToMany ? (
              <JunctionPicker
                dom={`${id}-${d.id}`}
                databaseName={d.name}
                tables={lookup.ofKind("table").filter((t) => t.database === d.id && !t.entity)}
                junction={tableOf(mapping?.junctionTable)}
                value={mapping?.junctionTable ?? ""}
                ends={ends.map((e) => ({ id: e.id, label: `${lookup.nameOf(endEntity(e)) ?? endEntity(e)} (${e.role})` }))}
                endKeys={mapping?.ends ?? []}
                loading={mappingsLoading}
                onTable={(table) => change((j) => setJunctionTable(j, table))}
                onEnd={(end, key) => change((j) => setEndForeignKey(j, end, key))}
              />
            ) : (
              <OlderShape mapping={mapping} />
            )}
            {!severalMode && tableOf(mapping?.junctionTable) && mapping ? (
              <LinkTableExtras
                dom={`${id}-${d.id}`}
                junction={tableOf(mapping.junctionTable)!}
                mapping={mapping}
                attributes={columnAttributes}
                change={(mutate) => change((j) => mutate(j as unknown as StorageMapping))}
                loading={mappingsLoading}
              />
            ) : null}
            {severalMode ? (
              <DatabaseRunLists
                databaseId={d.id}
                relationId={id}
                nameOf={(entity) => lookup.nameOf(entity) ?? entity}
                roleOf={(endId) => ends.find((e) => e.id === endId)?.role ?? endId}
              />
            ) : null}
            {nothingStored ? (
              <p className="text-12 text-secondary" data-testid="relation-storage-nothing-stored">
                Nothing is stored in this database.
              </p>
            ) : null}
            {problems.length ? (
              <ul className="flex flex-col gap-0.5 text-12" data-testid="relation-storage-problems">
                {problems.map((p, i) => {
                  const warning = p.severity === "warning";
                  const Icon = warning ? TriangleAlert : CircleAlert;
                  return (
                    <li key={i} className={`flex gap-1 ${warning ? "text-warning" : "text-danger"}`} data-severity={p.severity}>
                      <Icon className="mt-0.5 size-3.5 shrink-0" aria-hidden />
                      <span>
                        <span className="font-mono">{p.rule}</span> {p.message}
                      </span>
                    </li>
                  );
                })}
              </ul>
            ) : null}
            {summary ? <ElementLink summary={summary} secondary="relation mapping" pin={false} /> : null}
          </section>
        );
      })}
    </div>
  );
}

function ForeignKeyPicker({
  dom,
  databaseName,
  choices,
  reversed,
  dependentTable,
  value,
  loading,
  onPick,
}: {
  dom: string;
  databaseName: string;
  choices: ReturnType<typeof foreignKeyChoices>;
  /** Keys held the other way (by the principal end's table), which cannot realize the relationship. */
  reversed: number;
  /** The table that must hold the key (the dependent end's), when one must. */
  dependentTable: string | null;
  value: string;
  loading: boolean;
  onPick: (key: string | null) => void;
}) {
  return (
    <Field
      label="Foreign key"
      htmlFor={`${dom}-fk`}
      hint={
        loading
          ? "Loading the tables…"
          : choices.length
            ? `The key between the two bound tables that realizes this relationship${dependentTable ? `, held by ${dependentTable}` : ""}.`
            : reversed && dependentTable
              ? `The only key between the two tables is held the other way; this relationship needs one held by ${dependentTable}: add it there first.`
              : "No foreign key links the two bound tables yet: add one to a table first."
      }
    >
      <Select
        id={`${dom}-fk`}
        aria-label={`Foreign key in ${databaseName}`}
        className="w-96"
        data-testid="relation-foreign-key"
        value={value}
        disabled={loading}
        onChange={(e) => onPick(e.target.value || null)}
      >
        <option value="">(none named)</option>
        {value && !choices.some((c) => c.key.id === value) ? <option value={value}>{`${value} (not found)`}</option> : null}
        {choices.map((c) => (
          <option key={c.key.id} value={c.key.id}>
            {c.label}
          </option>
        ))}
      </Select>
    </Field>
  );
}

function JunctionPicker({
  dom,
  databaseName,
  tables,
  junction,
  value,
  ends,
  endKeys,
  loading,
  onTable,
  onEnd,
}: {
  dom: string;
  databaseName: string;
  tables: { id: string; name: string }[];
  junction: TableDocLike | undefined;
  value: string;
  ends: { id: string; label: string }[];
  endKeys: { end: string; foreignKey?: string | undefined }[];
  loading: boolean;
  onTable: (table: string | null) => void;
  onEnd: (end: string, key: string | null) => void;
}) {
  return (
    <div className="flex flex-wrap items-end gap-2">
      <Field label="Junction table" htmlFor={`${dom}-junction`}>
        <Select
          id={`${dom}-junction`}
          aria-label={`Junction table in ${databaseName}`}
          className="w-56"
          value={value}
          disabled={loading}
          onChange={(e) => onTable(e.target.value || null)}
          data-testid="relation-junction"
        >
          <option value="">(none named)</option>
          {tables.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
            </option>
          ))}
        </Select>
      </Field>
      {junction
        ? ends.map((e) => (
            <Field key={e.id} label={`Key for ${e.label}`} htmlFor={`${dom}-end-${e.id}`}>
              <Select
                id={`${dom}-end-${e.id}`}
                className="w-56"
                value={endKeys.find((k) => k.end === e.id)?.foreignKey ?? ""}
                disabled={loading}
                onChange={(ev) => onEnd(e.id, ev.target.value || null)}
              >
                <option value="">(none named)</option>
                {(junction.foreignKeys ?? []).map((k) => (
                  <option key={k.id} value={k.id}>
                    {k.name ?? k.id}
                  </option>
                ))}
              </Select>
            </Field>
          ))
        : null}
    </div>
  );
}

/** Ends not both bound: nothing to pick here. An older mapping's shape shows read only. */
function OlderShape({ mapping }: { mapping: RelationMappingDoc | null }) {
  const shape = mapping?.shape;
  return (
    <div className="flex flex-col gap-1" data-testid="relation-storage-unbound">
      <p className="text-12 text-secondary">Bind both ends to a table to name the foreign key.</p>
      {mapping?.ignore === true ? <p className="text-13">Left out of this database.</p> : null}
      {shape ? (
        <p className="text-12" data-testid="relation-storage-older-shape">
          {`Shape (an older mapping, read only): ${SHAPE_LABELS[shape]}${shape === "promoted" && mapping?.promotedName ? ` (${mapping.promotedName})` : ""}`}
        </p>
      ) : null}
    </div>
  );
}
