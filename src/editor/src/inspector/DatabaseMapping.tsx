// A database's Mapping section (D46, explorer-redesign.md 1.3): what the database holds and how. The convention
// (all domains, the picked domains, or nothing) is edited here; the entities mapped one by one are the mapping
// elements for this database, listed apart from the ones a mapping ignores (read from the mapping documents: the index
// row has no `ignore`). A file without `byConvention` shows "all domains (unspecified)" with Make explicit.
import { useMemo } from "react";
import { useElements, useIndex } from "@/api/queries";
import { indexLookup } from "@/model/index";
import { conventionLabel, conventionOf, setConvention, type ByConvention } from "@/model/databaseMapping";
import { schemasOf } from "@/model/databaseSchemas";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";

interface Props {
  id: string;
  json: Record<string, unknown>;
  edit: (update: (json: Record<string, unknown>) => void) => void;
  flush: () => void;
}

export function DatabaseMappingSection({ id, json, edit, flush }: Props) {
  const index = useIndex();
  const lookup = useMemo(() => indexLookup(index.data), [index.data]);
  const convention = conventionOf(json as Parameters<typeof conventionOf>[0]);
  const schemas = schemasOf(json);
  const nameOf = (x: string) => lookup.byId.get(x)?.name ?? x;
  // A domain shows as its path (Billing › Catalog), as in the New database dialog: two sub-domains may share a name.
  const pathOf = (domain: string) => {
    const names: string[] = [];
    for (let d: string | null | undefined = domain, guard = 0; d && guard < 64; guard++) {
      const row = lookup.byId.get(d);
      if (!row || row.kind !== "package") break;
      names.push(row.name);
      d = row.package;
    }
    return names.reverse().join(" › ");
  };
  const domains = lookup
    .ofKind("package")
    .map((p) => ({ id: p.id, path: pathOf(p.id) }))
    .sort((a, b) => a.path.localeCompare(b.path));
  const rows = lookup.ofKind("mapping").filter((m) => m.database === id && m.entity);
  const docs = useElements(rows.map((m) => m.id));
  const ignored = (mapping: string) => (docs.byId.get(mapping)?.json as { ignore?: unknown } | undefined)?.ignore === true;
  const names = (list: typeof rows) => list.map((m) => nameOf(m.entity!)).sort((a, b) => a.localeCompare(b));
  const mapped = names(rows.filter((m) => !ignored(m.id)));
  const ignoring = names(rows.filter((m) => ignored(m.id)));
  const write = (mode: ByConvention, packages: readonly string[] = convention.packages, bySchema: Record<string, string | null> = convention.schemas) => {
    edit((j) => setConvention(j, mode, packages, bySchema));
    flush();
  };

  return (
    <section className="col-span-2 flex flex-col gap-1" data-testid="database-mapping">
      <SectionTitle>Mapping</SectionTitle>
      <p className="text-12" data-testid="database-mapping-convention">
        {conventionLabel(convention, nameOf)}
      </p>
      {!convention.explicit ? (
        <Button size="sm" onClick={() => write(convention.mode)} data-testid="database-mapping-make-explicit">
          Make explicit
        </Button>
      ) : null}
      <Field label="Map domains by convention" htmlFor={`${id}-convention`}>
        <Select id={`${id}-convention`} value={convention.mode} onChange={(e) => write(e.target.value as ByConvention)}>
          <option value="none">None</option>
          <option value="packages">Pick domains</option>
          <option value="all">All domains</option>
        </Select>
      </Field>
      {convention.mode === "packages" ? (
        <fieldset className="flex flex-col" aria-label="Domains mapped by convention">
          {domains.map((d) => (
            <label key={d.id} className="flex h-6 items-center gap-2 text-12">
              <input
                type="checkbox"
                checked={convention.packages.includes(d.id)}
                onChange={(e) => write("packages", e.target.checked ? [...convention.packages, d.id] : convention.packages.filter((x) => x !== d.id))}
              />
              <span className="min-w-0 flex-1 truncate">{d.path}</span>
              {/* With several schemas, each convention domain may go to its own (erratum E26). */}
              {schemas.length > 1 && convention.packages.includes(d.id) ? (
                <select
                  className="h-5 rounded-sm border border-default bg-surface text-11"
                  aria-label={`Schema of ${d.path}`}
                  data-testid={`convention-schema-${d.id}`}
                  value={convention.schemas[d.id] ?? ""}
                  onChange={(e) => write("packages", convention.packages, { ...convention.schemas, [d.id]: e.target.value || null })}
                >
                  <option value="">Default schema</option>
                  {schemas.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
                </select>
              ) : null}
            </label>
          ))}
        </fieldset>
      ) : null}
      <p className="text-12 text-secondary" data-testid="database-mapping-mapped">
        {mapped.length ? `Mapped one by one: ${mapped.join(", ")}` : "No entity is mapped one by one."}
      </p>
      {ignoring.length ? (
        <p className="text-12 text-secondary" data-testid="database-mapping-ignored">
          {`Ignored (kept out of this database): ${ignoring.join(", ")}`}
        </p>
      ) : null}
    </section>
  );
}
