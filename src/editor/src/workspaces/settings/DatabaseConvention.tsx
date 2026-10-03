// Settings > Conventions, "Older projects: tables laid out by convention" (D46, explorer-redesign.md 1.3): the older way a
// database holds entities, by domain. The database side knows no entities and an entity's storage is said on its Storage tab
// (the owner, 2026-10-03), so nothing here adds a domain: the panel is read-only, one card per database whose convention still
// lays out tables, with **Stop laying out by convention** per domain (or for every domain, when the database takes them all).
// Stopping first offers to store the tables that convention lays out as table files (the engine's materialize-tables, one undo
// step), so nothing is lost; then the database's convention drops the domain (another undo step). The entities an older
// mapping element places one by one are listed, read-only, apart from the ones a mapping ignores (read from the mapping
// documents: the index row has no `ignore`).
import { useMemo, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { elementQuery, useDatabaseView, useElements, useIndex } from "@/api/queries";
import type { ElementSummary } from "@/api/types";
import { useServices } from "@/app/context";
import { indexLookup } from "@/model/index";
import { conventionLabel, conventionOf, setConvention, takesPackage, type DatabaseConvention } from "@/model/databaseMapping";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { SectionTitle, Spinner } from "@/components/ui/misc";
import { storeTables, useStorableTables } from "@/workspaces/database/storeTables";

type Json = Record<string, unknown>;
type Lookup = ReturnType<typeof indexLookup>;

/** A domain's path (Billing › Catalog): two sub-domains may share a name. */
function pathOf(lookup: Lookup, domain: string): string {
  const names: string[] = [];
  for (let d: string | null | undefined = domain, guard = 0; d && guard < 64; guard++) {
    const row = lookup.byId.get(d);
    if (!row || row.kind !== "package") break;
    names.push(row.name);
    d = row.package;
  }
  return names.reverse().join(" › ") || domain;
}

/** Whether a convention lays out anything (a file without the member lays out every domain). */
export const laysOutTables = (convention: DatabaseConvention): boolean =>
  convention.mode === "all" || (convention.mode === "packages" && convention.packages.length > 0);

/**
 * The convention once `domain` stops being laid out (null: every domain): none when nothing is left, else the other domains, each
 * keeping its schema.
 */
export function stopConvention(json: Json, domain: string | null): void {
  const convention = conventionOf(json as Parameters<typeof conventionOf>[0]);
  const rest = domain === null ? [] : convention.packages.filter((p) => p !== domain);
  if (!rest.length) setConvention(json, "none");
  else setConvention(json, "packages", rest, convention.schemas);
}

/** One database's card: what its convention lays out, Stop per domain, and its older mapping elements. */
export function DatabaseMappingSection({ id, name, json }: { id: string; name: string; json: Json }) {
  const index = useIndex();
  const lookup = useMemo(() => indexLookup(index.data), [index.data]);
  const convention = conventionOf(json as Parameters<typeof conventionOf>[0]);
  const nameOf = (x: string) => lookup.byId.get(x)?.name ?? x;
  const rows = lookup.ofKind("mapping").filter((m) => m.database === id && m.entity);
  const docs = useElements(rows.map((m) => m.id));
  const ignored = (mapping: string) => (docs.byId.get(mapping)?.json as { ignore?: unknown } | undefined)?.ignore === true;
  const names = (list: typeof rows) => list.map((m) => nameOf(m.entity!)).sort((a, b) => a.localeCompare(b));
  const mapped = names(rows.filter((m) => !ignored(m.id)));
  const ignoring = names(rows.filter((m) => ignored(m.id)));
  const [stopping, setStopping] = useState<{ domain: string | null; label: string } | null>(null);
  const domains =
    convention.mode === "all"
      ? [{ id: null, label: "Every domain" }]
      : convention.mode === "packages"
        ? convention.packages.map((p) => ({ id: p as string | null, label: pathOf(lookup, p) })).sort((a, b) => a.label.localeCompare(b.label))
        : [];

  return (
    <section className="flex flex-col gap-1" aria-label={`Database ${name}`} data-testid="database-mapping" data-database={name}>
      <p className="text-12" data-testid="database-mapping-convention">
        <span className="font-medium">{name}</span>: {conventionLabel(convention, nameOf)}
      </p>
      {domains.length ? (
        <ul className="flex flex-col" role="group" aria-label="Domains laid out by convention">
          {domains.map((d) => (
            <li key={d.id ?? "*"} className="flex h-6 items-center gap-2 text-12" data-testid="convention-domain">
              <span className="min-w-0 flex-1 truncate">{d.label}</span>
              <Button
                size="sm"
                variant="ghost"
                title={`Stop laying out ${d.id === null ? "every domain's" : `${d.label}'s`} tables in ${name} by convention`}
                onClick={() => setStopping({ domain: d.id, label: d.label })}
                data-testid="convention-stop"
              >
                Stop laying out by convention
              </Button>
            </li>
          ))}
        </ul>
      ) : null}
      <p className="text-12 text-secondary" data-testid="database-mapping-mapped">
        {mapped.length ? `Placed one by one by an older mapping element: ${mapped.join(", ")}` : "No older mapping element places an entity here."}
      </p>
      {ignoring.length ? (
        <p className="text-12 text-secondary" data-testid="database-mapping-ignored">
          {`Ignored (kept out of this database): ${ignoring.join(", ")}`}
        </p>
      ) : null}
      {stopping ? <StopDialog database={{ id, name }} domain={stopping.domain} label={stopping.label} onClose={() => setStopping(null)} /> : null}
    </section>
  );
}

/**
 * Stop laying out by convention: the tables that convention lays out for the domain, how many can be stored as table files now
 * (an inheritance hierarchy's, an abstract entity's and a junction table cannot yet), and the two ways through.
 */
function StopDialog({
  database,
  domain,
  label,
  onClose,
}: {
  database: { id: string; name: string };
  domain: string | null;
  label: string;
  onClose: () => void;
}) {
  const services = useServices();
  const qc = useQueryClient();
  const index = useIndex();
  const lookup = useMemo(() => indexLookup(index.data), [index.data]);
  const view = useDatabaseView(database.id);
  const tables = view.data?.view?.tables;
  const storable = useStorableTables(database.id, tables);
  const [busy, setBusy] = useState(false);
  const inDomain = (entity: string | null | undefined): boolean => {
    if (!entity) return false;
    if (domain === null) return true;
    const only: DatabaseConvention = { mode: "packages", explicit: true, packages: [domain], schemas: {} };
    return takesPackage(only, lookup.byId.get(entity)?.package, (p) => lookup.byId.get(p)?.package);
  };
  const owners = new Map([...storable].filter(([, owner]) => inDomain(owner)));
  const laidOut = (tables ?? []).filter(
    (t) => t.origin === "synthesized" && inDomain(t.entityId ?? (t.relationId ? relationOwner(lookup, t.relationId) : null)),
  );
  const left = Math.max(0, laidOut.length - owners.size);
  const stop = async (store: boolean) => {
    setBusy(true);
    try {
      if (store && owners.size) {
        const n = owners.size;
        const stored = await storeTables(services, qc, database.id, owners, `Store ${n === 1 ? "1 table" : `${n} tables`} as table files`);
        if (!stored.ok) return;
      }
      await services.drafts.flush(database.id);
      const base = await qc.fetchQuery({ ...elementQuery(database.id), staleTime: 0 });
      services.drafts.edit(database.id, (json) => void stopConvention(json as unknown as Json, domain), { base });
      await services.drafts.flush(database.id);
      services.store.getState().notify(`${database.name} no longer lays out ${domain === null ? "any domain" : label} by convention.`);
      onClose();
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(open) => !open && !busy && onClose()}>
      <DialogContent title="Stop laying out by convention" description={`${label} in ${database.name}.`}>
        <div className="flex flex-col gap-2 text-13" data-testid="convention-stop-dialog">
          {view.isPending ? (
            <Spinner label="Reading the database" />
          ) : (
            <>
              <p data-testid="convention-stop-summary">
                {laidOut.length === 0
                  ? "This convention lays out no table now."
                  : `${laidOut.length === 1 ? "1 table is" : `${laidOut.length} tables are`} laid out by this convention. ${
                      owners.size
                        ? `Store ${owners.size === 1 ? "it" : owners.size === laidOut.length ? "them" : `${owners.size} of them`} as table files first to keep ${owners.size === 1 ? "it" : "them"}: each entity is then bound to its table.`
                        : ""
                    }`}
              </p>
              {left ? (
                <p className="text-12 text-secondary" data-testid="convention-stop-left">
                  {left === 1 ? "1 table" : `${left} tables`} cannot be stored as table files yet: an inheritance hierarchy&apos;s or an abstract entity&apos;s
                  goes with the convention, and a junction table stays while both its ends are stored here.
                </p>
              ) : null}
            </>
          )}
          <div className="flex justify-end gap-2">
            {busy ? <Spinner label="Stopping" /> : null}
            <Button type="button" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button type="button" onClick={() => void stop(false)} disabled={busy || view.isPending} data-testid="convention-stop-only">
              Stop without storing
            </Button>
            {owners.size ? (
              <Button type="button" variant="primary" onClick={() => void stop(true)} disabled={busy} data-testid="convention-stop-store">
                Store as table files, then stop
              </Button>
            ) : null}
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

/** A junction table's relation stands for its first end's entity, so it counts with that entity's domain. */
function relationOwner(lookup: Lookup, relation: string): string | null {
  return lookup.byId.get(relation)?.ends?.[0]?.entity ?? null;
}

/**
 * The Conventions tab's section, for older projects only: each database whose convention still lays out tables, read-only, with
 * Stop laying out by convention. New work binds each entity on its Storage tab (Create tables…, Auto-map to existing tables…).
 */
export function TablesByConvention() {
  const index = useIndex();
  const databases = useMemo(() => indexLookup(index.data).ofKind("database"), [index.data]);
  const docs = useElements(databases.map((d) => d.id));
  const shown = databases
    .map((d) => ({ row: d, json: docs.byId.get(d.id)?.json as Json | undefined }))
    .filter((d): d is { row: ElementSummary; json: Json } => !!d.json && laysOutTables(conventionOf(d.json as Parameters<typeof conventionOf>[0])))
    .sort((a, b) => a.row.name.localeCompare(b.row.name));
  if (!databases.length || (!docs.pending && !shown.length)) return null;
  return (
    <section
      className="mt-4 flex max-w-2xl flex-col gap-2"
      aria-label="Older projects: tables laid out by convention"
      data-testid="settings-tables-by-convention"
    >
      <SectionTitle>Older projects: tables laid out by convention</SectionTitle>
      <p className="text-12 text-secondary">
        Before entities were bound to tables, a database laid out a table for each entity of the domains it listed. These databases still do. Stop it per
        domain, storing its tables as table files first; new work binds each entity on its Storage tab instead.
      </p>
      {docs.pending ? <Spinner label="Reading the databases" /> : null}
      {shown.map((d) => (
        <DatabaseMappingSection key={d.row.id} id={d.row.id} name={d.row.name} json={d.json} />
      ))}
    </section>
  );
}
