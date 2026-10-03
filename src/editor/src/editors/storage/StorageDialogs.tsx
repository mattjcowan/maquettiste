// The entity side's bulk storage actions (the owner, 2026-10-02: "map or not map (domain only), generate mappings from
// existing tables, auto-map to existing tables based on name matches, or create tables if desired, and do this in bulk"):
// Auto-map to existing tables…, Create tables…, Remove bindings… over entities or a domain, and New entities from tables… into
// a domain. Each previews what it will do and applies it as one batch and one undo step. Mounted once by the app shell; the
// Domain model explorer's menus and the Storage tab ask for them through the store (`requestStorage`).
import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys, useElements, useIndex, useSettings } from "@/api/queries";
import type { DatabaseDoc, ElementSummary, MaterializePlan } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Select } from "@/components/ui/input";
import { cn } from "@/lib/cn";
import { newId } from "@/lib/ids";
import { indexLookup } from "@/model/index";
import { schemasOf } from "@/model/databaseSchemas";
import { useEditor, type StorageRequest } from "@/state/store";
import { setView } from "@/editors/tabs";
import { applyMatches, autoMapRow, type AutoMapRow, type Binding } from "./fieldMap";
import {
  commitEntities,
  commitMaterialize,
  databasesOf,
  entitiesOfDomain,
  removeBindings,
  useBindingSources,
  useMaterializeStatus,
  useStorageAttributes,
} from "./useStorage";
import { STORAGE_LABELS } from "./labels";

type Json = Record<string, unknown>;

export function StorageDialogsHost() {
  const { store } = useServices();
  const request = useEditor(store, (s) => s.storageRequest);
  if (!request) return null;
  const close = () => store.getState().requestStorage(null);
  const key = `${request.action}:${request.nonce}`;
  switch (request.action) {
    case "create-tables":
      return <CreateTablesDialog key={key} request={request} onClose={close} />;
    case "entities-from-tables":
      return <EntitiesFromTablesDialog key={key} request={request} onClose={close} />;
    case "auto-map":
      return <AutoMapDialog key={key} request={request} onClose={close} />;
    case "remove-bindings":
      return <RemoveBindingsDialog key={key} request={request} onClose={close} />;
  }
}

/** The entities a request is about: the ones picked, else the domain's. */
function useRequestEntities(request: StorageRequest): string[] {
  const index = useIndex();
  return useMemo(
    () => (request.entities.length ? request.entities : request.domain ? entitiesOfDomain(index.data, request.domain).map((e) => e.id) : []),
    [request.entities, request.domain, index.data],
  );
}

function DatabasePicker({ value, onChange, id }: { value: string; onChange: (id: string) => void; id: string }) {
  const index = useIndex();
  const databases = databasesOf(index.data);
  return (
    <Field label="Database" htmlFor={id}>
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        {databases.map((d) => (
          <option key={d.id} value={d.id}>
            {d.name}
          </option>
        ))}
      </Select>
    </Field>
  );
}

function useFirstDatabase(request: StorageRequest): [string, (id: string) => void] {
  const index = useIndex();
  const first = request.database ?? databasesOf(index.data)[0]?.id ?? "";
  const [database, setDatabase] = useState(first);
  return [database || first, setDatabase];
}

function PlanView({ plan, pending }: { plan: MaterializePlan | undefined; pending: boolean }) {
  if (pending) return <p className="text-12 text-secondary">Planning…</p>;
  if (!plan) return null;
  const list = (title: string, items: MaterializePlan["creates"]) =>
    items.length ? (
      <section>
        <p className="text-11 font-semibold uppercase tracking-wide text-secondary">{title}</p>
        <ul className="text-12">
          {items.map((c) => (
            <li key={c.id}>
              <span className="text-secondary">{c.kind}</span> <span className="font-mono">{c.name}</span> <span className="text-secondary">: {c.because}</span>
            </li>
          ))}
        </ul>
      </section>
    ) : null;
  return (
    <div className="flex max-h-[40vh] flex-col gap-1 overflow-auto" data-testid="storage-plan">
      {plan.diagnostics.map((d, i) => (
        <p key={i} role="alert" className="text-12 text-danger">
          <span className="font-mono">{d.rule}</span> {d.message}
        </p>
      ))}
      {list("Creates", plan.creates)}
      {list("Updates", plan.updates)}
      {list("Deletes", plan.deletes)}
      {plan.notes.length ? (
        <ul className="text-12 text-secondary">
          {plan.notes.map((n, i) => (
            <li key={i}>{n}</li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function Footer({
  onClose,
  onApply,
  disabled,
  busy,
  label,
  testId,
}: {
  onClose: () => void;
  onApply: () => void;
  disabled: boolean;
  busy: boolean;
  label: string;
  testId: string;
}) {
  return (
    <div className="flex justify-end gap-2">
      <Button onClick={onClose}>Cancel</Button>
      <Button variant="primary" disabled={disabled || busy} onClick={onApply} data-testid={testId}>
        {busy ? "Applying…" : label}
      </Button>
    </div>
  );
}

/** After an apply, the entity editor (if one is open) shows its Storage tab. */
function useShowStorage() {
  const { store } = useServices();
  return () => store.getState().updateEditors((e) => setView(e, "entity", "storage"));
}

// ------------------------------------------------------------------ Create tables…

function CreateTablesDialog({ request, onClose }: { request: StorageRequest; onClose: () => void }) {
  const services = useServices();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const entities = useRequestEntities(request);
  const [database, setDatabase] = useFirstDatabase(request);
  const dbDoc = useElements(database ? [database] : []).byId.get(database)?.json as (DatabaseDoc & Json) | undefined;
  const schemas = schemasOf(dbDoc);
  const [schema, setSchema] = useState("");
  const status = useMaterializeStatus(database || null);
  // Only the entities with no binding to the database yet.
  const unbound = entities.filter((e) => status.data?.entities.some((s) => s.id === e));
  const plan = useQuery({
    queryKey: ["storage-plan", "tables", database, schema, unbound.join(",")],
    queryFn: () => endpoints.previewMaterialize(database, { op: "materialize-tables", entities: unbound, schema: schema || null }),
    enabled: !!database && unbound.length > 0,
    staleTime: 0,
    gcTime: 0,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const showStorage = useShowStorage();
  const skipped = entities.length - unbound.length;
  const single = entities.length === 1;
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent
        title={single ? `Create a table for ${lookup.nameOf(entities[0]) ?? "this entity"}` : `Create tables for ${entities.length} entities`}
        description="Each entity gets a table of its own in the database, shaped as its conventions would make it, and a binding to it. The table is then yours to edit."
        wide
      >
        <div className="flex flex-col gap-2" data-testid="create-tables-dialog">
          <div className="flex flex-wrap gap-2">
            <DatabasePicker id="create-tables-database" value={database} onChange={setDatabase} />
            {schemas.length ? (
              <Field label="Schema" htmlFor="create-tables-schema">
                <Select id="create-tables-schema" value={schema} onChange={(e) => setSchema(e.target.value)}>
                  <option value="">Each table's own (the conventions')</option>
                  {schemas.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
                </Select>
              </Field>
            ) : null}
          </div>
          {skipped ? (
            <p className="text-12 text-secondary">
              {skipped === 1 ? "1 entity is" : `${skipped} entities are`} already bound to this database and left as {skipped === 1 ? "it is" : "they are"}.
            </p>
          ) : null}
          {!unbound.length && !status.isPending ? <p className="text-12 text-secondary">Nothing to create here.</p> : null}
          <PlanView plan={plan.data} pending={plan.isFetching} />
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <Footer
            onClose={onClose}
            busy={busy}
            label={unbound.length === 1 ? "Create the table" : `Create ${unbound.length} tables`}
            testId="create-tables-apply"
            disabled={!plan.data?.valid || !unbound.length}
            onApply={async () => {
              if (!plan.data) return;
              setBusy(true);
              const failed = await commitMaterialize(
                services,
                unbound.length === 1 ? `Create a table for ${lookup.nameOf(unbound[0])}` : `Create ${unbound.length} tables`,
                { op: "materialize-tables", database, entities: unbound, ...(schema ? { schema } : {}) },
                { updates: plan.data.updates.map((u) => u.id), deletes: plan.data.deletes.map((d) => d.id) },
              );
              setBusy(false);
              if (failed) setError(failed);
              else {
                showStorage();
                onClose();
              }
            }}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}

// ------------------------------------------------------------------ New entities from tables…

function EntitiesFromTablesDialog({ request, onClose }: { request: StorageRequest; onClose: () => void }) {
  const services = useServices();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const [database, setDatabase] = useFirstDatabase(request);
  const packages = lookup
    .ofKind("package")
    .slice()
    .sort((a, b) => a.name.localeCompare(b.name));
  const [pkg, setPkg] = useState(request.domain ?? packages[0]?.id ?? "");
  const status = useMaterializeStatus(database || null);
  const free = (status.data?.sources ?? []).filter((s) => !s.boundBy.length);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  useEffect(() => setPicked(new Set()), [database]);
  const tables = free.filter((s) => picked.has(s.id)).map((s) => s.id);
  const plan = useQuery({
    queryKey: ["storage-plan", "entities", database, pkg, tables.join(",")],
    queryFn: () => endpoints.previewMaterialize(database, { op: "materialize-entities", tables, package: pkg }),
    enabled: !!database && !!pkg && tables.length > 0,
    staleTime: 0,
    gcTime: 0,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { reveal } = useEditorNavigation();
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent
        title={STORAGE_LABELS.entitiesFromTables.replace("…", "")}
        description="An entity per table or view, one attribute per column, bound to it; a foreign key between the picked tables becomes a relationship."
        wide
      >
        <div className="flex flex-col gap-2" data-testid="entities-from-tables-dialog">
          <div className="flex flex-wrap gap-2">
            <DatabasePicker id="entities-from-tables-database" value={database} onChange={setDatabase} />
            <Field label="Domain" htmlFor="entities-from-tables-domain">
              <Select id="entities-from-tables-domain" value={pkg} onChange={(e) => setPkg(e.target.value)}>
                {packages.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <section className="flex flex-col gap-0.5">
            <div className="flex items-center gap-2">
              <p className="text-11 font-semibold uppercase tracking-wide text-secondary">Tables and views no entity is bound to</p>
              <Button size="sm" variant="ghost" disabled={!free.length} onClick={() => setPicked(new Set(free.map((s) => s.id)))}>
                Select all
              </Button>
            </div>
            {status.isPending ? <p className="text-12 text-secondary">Loading…</p> : null}
            {!status.isPending && !free.length ? <p className="text-12 text-secondary">Every table and view of this database has an entity.</p> : null}
            <ul className="flex max-h-[30vh] flex-col overflow-auto" data-testid="entities-from-tables-list">
              {free.map((s) => (
                <li key={s.id}>
                  <label className="flex h-6 items-center gap-2 text-13">
                    <input
                      type="checkbox"
                      checked={picked.has(s.id)}
                      onChange={(e) => {
                        const next = new Set(picked);
                        if (e.target.checked) next.add(s.id);
                        else next.delete(s.id);
                        setPicked(next);
                      }}
                    />
                    <span className="font-mono">{s.schema ? `${s.schema}.${s.name}` : s.name}</span>
                    <span className="text-12 text-secondary">{s.kind === "view" ? "view" : s.origin}</span>
                  </label>
                </li>
              ))}
            </ul>
          </section>
          <PlanView plan={plan.data} pending={plan.isFetching} />
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <Footer
            onClose={onClose}
            busy={busy}
            label={tables.length === 1 ? "Create the entity" : `Create ${tables.length} entities`}
            testId="entities-from-tables-apply"
            disabled={!plan.data?.valid || !tables.length}
            onApply={async () => {
              if (!plan.data) return;
              setBusy(true);
              const failed = await commitMaterialize(
                services,
                `New entities from ${tables.length === 1 ? "a table" : `${tables.length} tables`}`,
                { op: "materialize-entities", database, tables, package: pkg },
                { updates: plan.data.updates.map((u) => u.id), deletes: plan.data.deletes.map((d) => d.id) },
              );
              setBusy(false);
              if (failed) setError(failed);
              else {
                const first = plan.data.creates.find((c) => c.kind === "entity");
                const row = first ? indexLookup(services.queryClient.getQueryData<ElementSummary[]>(keys.index)).byId.get(first.id) : undefined;
                if (row) reveal(row);
                onClose();
              }
            }}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}

// ------------------------------------------------------------------ Auto-map to existing tables…

function AutoMapDialog({ request, onClose }: { request: StorageRequest; onClose: () => void }) {
  const services = useServices();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const settings = useSettings();
  const entities = useRequestEntities(request);
  const [database, setDatabase] = useFirstDatabase(request);
  const { sources, pending } = useBindingSources(database || null);
  const { docs, attributes, pending: loading } = useStorageAttributes(entities);
  const dbName = lookup.nameOf(database) ?? "";
  const conventions =
    (settings.data?.settings as
      { conventions?: { pluralTables?: boolean | null }; databases?: Record<string, { pluralTables?: boolean | null }> } | undefined) ?? {};
  const pluralTables = conventions.databases?.[dbName]?.pluralTables ?? conventions.conventions?.pluralTables ?? true;
  const [picks, setPicks] = useState<Record<string, string | null>>({});
  const [excluded, setExcluded] = useState<Record<string, boolean>>({});
  useEffect(() => {
    setPicks({});
    setExcluded({});
  }, [database]);
  const rows = useMemo(
    () =>
      entities.map((id) => {
        const bound = ((docs.get(id)?.bindings as Binding[] | undefined) ?? []).some((b) => b.database === database);
        const row = autoMapRow({ id, name: lookup.nameOf(id) ?? id }, attributes.get(id) ?? [], sources, { pluralTables, picked: picks[id] ?? null });
        return { row, bound };
      }),
    [entities, docs, attributes, sources, pluralTables, picks, database, lookup],
  );
  const included = (r: { row: AutoMapRow; bound: boolean }) =>
    !r.bound && !!r.row.source && (excluded[r.row.entity] === undefined ? true : !excluded[r.row.entity]);
  const chosen = rows.filter(included);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const showStorage = useShowStorage();
  const counts = {
    match: rows.filter((r) => !r.bound && r.row.outcome === "match").length,
    partial: rows.filter((r) => !r.bound && r.row.outcome === "partial").length,
    miss: rows.filter((r) => !r.bound && r.row.outcome === "miss").length,
  };
  const choices = sources.filter((s) => !s.projected);
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent
        title={STORAGE_LABELS.autoMap.replace("…", "")}
        description="Each entity is matched to a table by name through the naming conventions, then its attributes to the table's columns by name. Review the list, pick a table for a miss, then apply: each included entity gets a binding."
        wide
      >
        <div className="flex flex-col gap-2" data-testid="auto-map-dialog">
          <div className="flex flex-wrap items-end gap-2">
            <DatabasePicker id="auto-map-database" value={database} onChange={setDatabase} />
            <p className="pb-1 text-12 text-secondary" data-testid="auto-map-counts">
              {counts.match} matched, {counts.partial} partly matched, {counts.miss} without a table
            </p>
          </div>
          {pending || loading ? <p className="text-12 text-secondary">Loading…</p> : null}
          <div className="max-h-[50vh] overflow-auto">
            <table className="w-full text-13" aria-label="Entities and the tables they map to" data-testid="auto-map-rows">
              <thead className="text-left text-11 text-secondary">
                <tr>
                  <th className="w-8 font-medium">Include</th>
                  <th className="font-medium">Entity</th>
                  <th className="font-medium">Source</th>
                  <th className="font-medium">Match</th>
                  <th className="font-medium">Attributes</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.row.entity} data-testid={`auto-map-row-${r.row.entityName}`} data-outcome={r.bound ? "bound" : r.row.outcome}>
                    <td>
                      <input
                        type="checkbox"
                        aria-label={`Include ${r.row.entityName}`}
                        disabled={r.bound || !r.row.source}
                        checked={included(r)}
                        onChange={(e) => setExcluded({ ...excluded, [r.row.entity]: !e.target.checked })}
                      />
                    </td>
                    <td className="font-mono">{r.row.entityName}</td>
                    <td className="w-64 py-0.5">
                      {r.bound ? (
                        <span className="text-12 text-secondary">already bound to {dbName}</span>
                      ) : (
                        <Select
                          aria-label={`Source of ${r.row.entityName}`}
                          className="h-6"
                          value={r.row.source?.id ?? ""}
                          onChange={(e) => {
                            setPicks({ ...picks, [r.row.entity]: e.target.value || null });
                            setExcluded({ ...excluded, [r.row.entity]: false });
                          }}
                        >
                          <option value="">(no table)</option>
                          {choices.map((s) => (
                            <option key={s.id} value={s.id}>
                              {s.name}
                              {s.kind !== "table" ? ` (${s.kind})` : s.projected ? " (projected)" : ""}
                            </option>
                          ))}
                        </Select>
                      )}
                    </td>
                    <td className={cn("text-12", r.row.outcome === "match" ? "text-success" : r.row.outcome === "partial" ? "text-warning" : "text-secondary")}>
                      {r.bound ? "" : { match: "match", partial: "partial match", miss: "no table by that name" }[r.row.outcome]}
                    </td>
                    <td className="text-12 text-secondary">
                      {r.row.source && !r.bound
                        ? `${r.row.matches.length} mapped${r.row.missing.length ? `; not found: ${r.row.missing.map((a) => a.name).join(", ")}` : ""}`
                        : ""}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <Footer
            onClose={onClose}
            busy={busy}
            label={chosen.length === 1 ? "Bind 1 entity" : `Bind ${chosen.length} entities`}
            testId="auto-map-apply"
            disabled={!chosen.length || pending || loading}
            onApply={async () => {
              setBusy(true);
              const byEntity = new Map(chosen.map((r) => [r.row.entity, r.row]));
              const failed = await commitEntities(
                services,
                `Auto-map ${chosen.length === 1 ? chosen[0].row.entityName : `${chosen.length} entities`} to ${dbName}`,
                [...byEntity.keys()],
                (json) => {
                  const row = byEntity.get(String(json.id));
                  if (!row?.source) return null;
                  const list = (json.bindings as Binding[] | undefined) ?? [];
                  if (list.some((b) => b.database === database)) return null;
                  const binding: Binding = { id: newId(), database, source: row.source.id };
                  applyMatches(binding, row.matches, row.source.columns);
                  json.bindings = [...list, binding];
                  return json;
                },
              );
              setBusy(false);
              if (failed) setError(failed);
              else {
                showStorage();
                onClose();
              }
            }}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}

// ------------------------------------------------------------------ Remove bindings…

function RemoveBindingsDialog({ request, onClose }: { request: StorageRequest; onClose: () => void }) {
  const services = useServices();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const entities = useRequestEntities(request);
  const docs = useElements(entities);
  const [database, setDatabase] = useState<string>(request.database ?? "");
  const bindingsOf = (id: string) => ((docs.byId.get(id)?.json as Json | undefined)?.bindings as Binding[] | undefined) ?? [];
  const affected = entities.filter((id) => bindingsOf(id).some((b) => !database || b.database === database));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent
        title={STORAGE_LABELS.removeBindings.replace("…", "")}
        description="The entities become domain only in that database; the tables, views and queries stay."
      >
        <div className="flex flex-col gap-2" data-testid="remove-bindings-dialog">
          <Field label="Database" htmlFor="remove-bindings-database">
            <Select id="remove-bindings-database" value={database} onChange={(e) => setDatabase(e.target.value)}>
              <option value="">Every database</option>
              {databasesOf(index.data).map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name}
                </option>
              ))}
            </Select>
          </Field>
          {docs.pending ? <p className="text-12 text-secondary">Loading…</p> : null}
          {!docs.pending && !affected.length ? <p className="text-12 text-secondary">None of these entities has a binding there.</p> : null}
          <ul className="flex max-h-[40vh] flex-col overflow-auto text-13" data-testid="remove-bindings-list">
            {affected.map((id) => (
              <li key={id}>
                <span className="font-mono">{lookup.nameOf(id)}</span>{" "}
                <span className="text-12 text-secondary">
                  {bindingsOf(id)
                    .filter((b) => !database || b.database === database)
                    .map((b) => lookup.nameOf(b.database) ?? b.database)
                    .join(", ")}
                </span>
              </li>
            ))}
          </ul>
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <Footer
            onClose={onClose}
            busy={busy}
            label={affected.length === 1 ? "Remove 1 binding" : `Remove from ${affected.length} entities`}
            testId="remove-bindings-apply"
            disabled={!affected.length}
            onApply={async () => {
              setBusy(true);
              const failed = await removeBindings(services, affected, database || null);
              setBusy(false);
              if (failed) setError(failed);
              else onClose();
            }}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
