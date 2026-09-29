// The Mappings workspace (phase2-design.md 4.8): for the selected entity and database, the
// entity's attributes beside its table's columns (from the database view), joined by
// attributeId/attributePath. Convention-derived columns are muted; columns the entity's mapping
// element overrides are highlighted with the accent. Editing an override creates or saves the
// mapping element for that entity and database.
import { useEffect, useMemo, useRef, useState } from "react";
import { ArrowRight } from "lucide-react";
import { applySaveResult, useDatabaseView, useElements, useIndex, useProject } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { AttributeDoc, EntityDoc, MappingAttributeDoc, MappingDoc, ModelJson, StereotypeDoc } from "@/api/types";
import { indexLookup } from "@/model/index";
import { isBuiltin, typeLabel } from "@/model/model";
import { newId } from "@/lib/ids";
import { cn } from "@/lib/cn";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Toolbar, EmptyState, Spinner, Badge } from "@/components/ui/misc";
import { Input, Select } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import { useDraftDocument } from "@/inspector/useDraft";
import {
  applyAttributeOverride,
  applyEntityOverride,
  isEmptyPatch,
  mappingRows,
  otherColumns as unmatchedColumns,
  PendingCreates,
  storageOptions as storageFor,
  type AttributePatch,
  type EntityPatch,
} from "./model";

export function MappingsWorkspace() {
  const services = useServices();
  const { store, drafts, queryClient } = services;
  const index = useIndex();
  const project = useProject();
  const lookup = useMemo(() => indexLookup(index.data), [index.data]);
  const selection = useEditor(store, (s) => s.selection);
  const { select } = useEditorNavigation();
  const entities = lookup.ofKind("entity");
  const databases = useMemo(() => project.data?.databases ?? [], [project.data]);
  const selectedEntity = selection.find((id) => lookup.byId.get(id)?.kind === "entity") ?? null;
  const [entityId, setEntityId] = useState<string | null>(selectedEntity);
  const [databaseId, setDatabaseId] = useState<string | null>(null);

  useEffect(() => {
    if (selectedEntity) setEntityId(selectedEntity);
  }, [selectedEntity]);
  useEffect(() => {
    if (!entityId && entities.length) setEntityId(entities[0].id);
    if (!databaseId && databases.length) setDatabaseId(databases[0].id);
  }, [entityId, databaseId, entities, databases]);

  const entity = useDraftDocument(entityId);
  const view = useDatabaseView(databaseId);
  const mappingIds = useMemo(() => lookup.ofKind("mapping").map((m) => m.id), [lookup]);
  const mappingDocs = useElements(mappingIds);
  const stereotypeIds = useMemo(() => lookup.ofKind("stereotype").map((s) => s.id), [lookup]);
  const stereotypeDocs = useElements(stereotypeIds);
  const draftsState = useEditor(store, (s) => s.drafts);
  const mapping = useMemo(() => {
    for (const id of mappingIds) {
      const json = (draftsState[id]?.json ?? mappingDocs.byId.get(id)?.json) as unknown as MappingDoc | undefined;
      if (json && json.entity === entityId && json.database === databaseId) return json;
    }
    return null;
  }, [mappingIds, mappingDocs.byId, draftsState, entityId, databaseId]);
  const [creating, setCreating] = useState(false);

  const entityJson = entity.json as EntityDoc | undefined;
  const attributes: (AttributeDoc & { from?: string })[] = useMemo(() => {
    const own = entityJson?.attributes ?? [];
    const virtual = (entityJson?.stereotypes ?? []).flatMap((key) => {
      const doc = [...stereotypeDocs.byId.values()].find((d) => (d.json as unknown as StereotypeDoc).key === key);
      return ((doc?.json as unknown as StereotypeDoc | undefined)?.attributes ?? []).map((a) => ({ ...a, from: key }));
    });
    return [...own, ...virtual];
  }, [entityJson, stereotypeDocs.byId]);
  const table = view.data?.view?.tables.find((t) => t.entityId === entityId && !t.isJunction) ?? null;
  const rows = useMemo(() => mappingRows(attributes, table, mapping), [attributes, table, mapping]);

  const editAttribute = (attributeId: string, patch: AttributePatch | null) =>
    editMapping((m) => applyAttributeOverride(m, attributeId, patch), isEmptyPatch(patch));
  const editEntity = (patch: EntityPatch) => editMapping((m) => applyEntityOverride(m, patch), isEmptyPatch(patch));

  // Quick edits on an unmapped entity must create one mapping, not one per edit.
  const pendingCreates = useRef(new PendingCreates());
  useEffect(() => {
    if (mapping && entityId && databaseId) pendingCreates.current.forget(`${entityId}|${databaseId}`);
  }, [mapping, entityId, databaseId]);

  const editMapping = async (applyTo: (m: MappingDoc) => void, empty: boolean) => {
    if (!entityJson || !databaseId || !entityId) return;
    if (mapping) {
      drafts.edit(mapping.id, (json) => applyTo(json as unknown as MappingDoc));
      void drafts.flush(mapping.id);
      return;
    }
    const key = `${entityId}|${databaseId}`;
    const editCreated = (id: string) => {
      drafts.edit(id, (json) => applyTo(json as unknown as MappingDoc));
      return drafts.flush(id);
    };
    if (empty && !pendingCreates.current.has(key)) return;
    await pendingCreates.current.run(key, () => createMapping(applyTo), editCreated);
  };

  const createMapping = async (applyTo: (m: MappingDoc) => void): Promise<string | null> => {
    if (!entityJson || !databaseId || !entityId) return null;
    setCreating(true);
    try {
      const dbName = databases.find((d) => d.id === databaseId)?.name ?? "database";
      const json = {
        kind: "mapping",
        id: newId(),
        name: `${entityJson.name} in ${dbName}`,
        database: databaseId,
        entity: entityId,
      } as unknown as MappingDoc;
      applyTo(json);
      const result = await endpoints.createElement(json as unknown as ModelJson);
      if (result.outcome === "saved") {
        applySaveResult(queryClient, result);
        store.getState().pushUndo({
          label: `Map ${entityJson.name}`,
          ids: [json.id],
          before: [null],
          after: [result.current!.json as ModelJson],
          afterHashes: [result.hash],
        });
        return json.id;
      }
      store.getState().notify(`The mapping was not created: ${result.diagnostics[0]?.message ?? result.outcome}`, "error");
      return null;
    } finally {
      setCreating(false);
    }
  };

  const nameOf = (id: string) => lookup.nameOf(id);
  if (index.isPending || project.isPending) return <EmptyState title="Loading…" />;
  if (!entities.length || !databases.length) return <EmptyState title="Mappings need an entity and a database" />;

  const otherColumns = unmatchedColumns(table, rows);
  const designedTables = view.data?.view?.tables.filter((t) => t.origin !== "synthesized" && !t.isJunction) ?? [];

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="mappings-workspace">
      <Toolbar label="Mapping">
        <label htmlFor="mapping-entity" className="text-12 text-secondary">
          Entity
        </label>
        <Select
          id="mapping-entity"
          className="w-48"
          value={entityId ?? ""}
          onChange={(e) => {
            setEntityId(e.target.value);
            select([e.target.value]);
          }}
        >
          {entities.map((e) => (
            <option key={e.id} value={e.id}>
              {e.name}
            </option>
          ))}
        </Select>
        <label htmlFor="mapping-database" className="text-12 text-secondary">
          Database
        </label>
        <Select id="mapping-database" className="w-40" value={databaseId ?? ""} onChange={(e) => setDatabaseId(e.target.value)}>
          {databases.map((d) => (
            <option key={d.id} value={d.id}>
              {d.name}
            </option>
          ))}
        </Select>
        <span className="ml-auto flex items-center gap-2 text-12 text-secondary">
          {mapping ? <Badge tone="accent">Mapping: {mapping.name}</Badge> : <Badge>Conventions only</Badge>}
          {creating ? <Spinner label="Saving" /> : null}
        </span>
      </Toolbar>
      <div
        role="group"
        aria-label="Entity mapping"
        className={cn("flex flex-wrap items-center gap-3 border-b border-default px-4 py-2 text-12", mapping && "bg-accent-subtle")}
        data-testid="entity-mapping"
      >
        <label htmlFor="mapping-table" className="text-secondary">
          Table
        </label>
        <Select
          id="mapping-table"
          className="h-7 w-48 text-12"
          value={mapping?.table ?? ""}
          onChange={(e) => void editEntity({ table: e.target.value || undefined })}
        >
          <option value="">mapped automatically</option>
          {designedTables.map((t) => (
            <option key={t.key} value={t.key}>
              {t.schema ? `${t.schema}.` : ""}
              {t.name}
            </option>
          ))}
        </Select>
        <label htmlFor="mapping-inheritance" className="text-secondary">
          Inheritance
        </label>
        <Select
          id="mapping-inheritance"
          className="h-7 w-32 text-12"
          value={mapping?.inheritance ?? ""}
          onChange={(e) => void editEntity({ inheritance: (e.target.value || undefined) as EntityPatch["inheritance"] })}
        >
          <option value="">convention</option>
          <option value="tph">table per hierarchy</option>
          <option value="tpt">table per type</option>
          <option value="tpc">table per concrete type</option>
        </Select>
        <span className="flex items-center gap-1.5">
          <Checkbox
            id="mapping-ignore"
            checked={mapping?.ignore === true}
            onCheckedChange={(v) => void editEntity({ ignore: v === true ? true : undefined })}
          />
          <label htmlFor="mapping-ignore">Not stored in this database</label>
        </span>
      </div>
      <div className="min-h-0 flex-1 overflow-auto p-4">
        {view.isPending ? (
          <Spinner label="Resolving tables" />
        ) : !table ? (
          <EmptyState title="No table for this entity in this database">
            {view.data?.view ? "The entity is abstract, ignored, or outside the database's domains." : "The model has errors."}
          </EmptyState>
        ) : (
          <div className="flex flex-col gap-4">
            <p className="text-13">
              <span className="font-semibold">{entityJson?.name}</span> maps to table{" "}
              <span className="font-mono">
                {table.schema ? `${table.schema}.` : ""}
                {table.name}
              </span>{" "}
              <span className="text-secondary">({table.origin})</span>
            </p>
            <table className="w-full border-collapse text-12" aria-label="Attributes and their columns">
              <thead>
                <tr className="text-left text-11 text-secondary">
                  <th colSpan={2} scope="colgroup" className="py-1 font-semibold">
                    Entity {entityJson?.name}
                  </th>
                  <th />
                  <th colSpan={4} scope="colgroup" className="py-1 font-semibold">
                    Table {table.name}
                    <span className="ml-2 font-normal">muted: from conventions; highlighted: overridden by the mapping</span>
                  </th>
                </tr>
                <tr className="border-b border-default text-left text-11 text-secondary">
                  <th className="py-1 font-semibold">Attribute</th>
                  <th className="py-1 font-semibold">Type</th>
                  <th />
                  <th className="py-1 font-semibold">Columns</th>
                  <th className="py-1 font-semibold">Storage</th>
                  <th className="py-1 font-semibold">Prefix</th>
                  <th className="py-1 font-semibold">Ignore</th>
                </tr>
              </thead>
              <tbody>
                {rows.map(({ attribute: a, override, columns, state }) => {
                  const ref = isBuiltin(a.type) ? null : lookup.byId.get((a.type as { ref: string }).ref);
                  const storageOptions = storageFor(ref?.kind);
                  return (
                    <tr
                      key={a.id}
                      className={cn("border-b border-default", override && "border-l-2 border-l-accent bg-accent-subtle")}
                      data-testid={`mapping-row-${a.name}`}
                      data-overridden={override ? "true" : "false"}
                      data-state={state}
                    >
                      <td className="py-1 pl-2">
                        {a.name}
                        {a.from ? <span className="ml-1 text-11 text-secondary">«{a.from}»</span> : null}
                      </td>
                      <td className="py-1 font-mono text-11 text-secondary">{typeLabel(a, nameOf)}</td>
                      <td aria-hidden>
                        <ArrowRight className="size-3.5 text-secondary" />
                      </td>
                      <td className={cn("py-1 font-mono", override ? "text-primary" : "text-secondary")}>
                        {columns.length ? columns.map((c) => `${c.name} ${c.nativeType}`).join(", ") : <span className="italic">not stored</span>}
                      </td>
                      <td className="py-1 pr-2">
                        {storageOptions.length ? (
                          <Select
                            aria-label={`Storage of ${a.name}`}
                            className="h-7 w-28 text-12"
                            value={override?.storage ?? ""}
                            onChange={(e) =>
                              void editAttribute(a.id, {
                                storage: (e.target.value || undefined) as MappingAttributeDoc["storage"],
                              })
                            }
                          >
                            <option value="">convention</option>
                            {storageOptions.map((s) => (
                              <option key={s} value={s}>
                                {s}
                              </option>
                            ))}
                          </Select>
                        ) : null}
                      </td>
                      <td className="py-1 pr-2">
                        {ref?.kind === "value-object" ? (
                          <Input
                            aria-label={`Column prefix of ${a.name}`}
                            className="h-7 w-28 font-mono text-12"
                            placeholder={`${a.name}_`}
                            value={override?.prefix ?? ""}
                            onChange={(e) => void editAttribute(a.id, { prefix: e.target.value || undefined })}
                          />
                        ) : null}
                      </td>
                      <td className="py-1">
                        {a.from ? null : (
                          <Checkbox
                            aria-label={`Ignore ${a.name}`}
                            checked={override?.ignore === true}
                            onCheckedChange={(v) => void editAttribute(a.id, { ignore: v === true ? true : undefined })}
                          />
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {otherColumns.length ? (
              <div>
                <h3 className="mb-1 text-11 font-semibold uppercase tracking-wide text-secondary">Relationship, order and key columns</h3>
                <ul className="font-mono text-12 text-secondary">
                  {otherColumns.map((c) => (
                    <li key={c.key}>
                      {c.name} {c.nativeType}
                      {c.isForeignKey ? " (foreign key)" : ""}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
          </div>
        )}
      </div>
    </div>
  );
}
