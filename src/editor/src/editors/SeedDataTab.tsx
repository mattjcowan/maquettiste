// The Seed data tab of the entity and relationship editors, and the table editor's Data tab (table seeds): the Rows grid of
// the Reference data screen over the element's seeds, with each seed's settings under it (add, edit and delete rows by keyboard, CSV import with a preview, CSV export), end cells picked
// from the far entity's rows. With no seed yet, New seed creates one on request: nothing is created by itself. A table that
// holds the rows of reference types (their bindings, 2026-10-11) names them above the grid, each a link to the type.
import { useState } from "react";
import { Plus } from "lucide-react";
import { useIndex, useReferences } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { SeedGrid } from "@/workspaces/reference-data/RowsGrid";
import { createTargetSeed, useSeedTarget } from "@/workspaces/reference-data/seedTargets";
import { SeedSettings } from "./SeedSettings";

export function SeedDataTab({ id }: { id: string }) {
  const services = useServices();
  const { target, columns, seeds, endOptions } = useSeedTarget(id);
  const [creating, setCreating] = useState(false);
  if (!target) return <Spinner />;
  const create = async () => {
    setCreating(true);
    try {
      return await createTargetSeed(services, id);
    } finally {
      setCreating(false);
    }
  };
  if (!seeds.length)
    return (
      <>
        <BoundTypesNote id={id} />
        <EmptyState title="No seed data">
          <p>
            Seed data is the rows {target.name} starts with. It is kept with the model; how it is written out is up to the generator (sql-ddl&apos;s seed
            scripts, the seed-data pack&apos;s files).
          </p>
          <Button className="mt-2" size="sm" disabled={creating || !columns} onClick={() => void create()} data-testid="new-seed">
            <Plus /> New seed
          </Button>
        </EmptyState>
      </>
    );
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="seed-data-grid">
      <BoundTypesNote id={id} />
      <div className="min-h-0 flex-1">
        <SeedGrid key={id} name={target.name} columns={columns} seeds={seeds} keepStoredColumns endOptions={endOptions} createSeed={create} />
      </div>
      <SeedSettings seeds={seeds} target={id} />
    </div>
  );
}

/** The reference types whose bindings name this table: their rows are the table's rows too, before its own seeds. */
function BoundTypesNote({ id }: { id: string }) {
  const index = useIndex();
  const isTable = index.data?.some((r) => r.id === id && r.kind === "table");
  const references = useReferences(isTable ? id : null);
  const types = [...new Set((references.data ?? []).filter((r) => /^\/bindings\/\d+\/table$/.test(r.jsonPointer)).map((r) => r.fromElementId))]
    .map((typeId) => index.data?.find((r) => r.id === typeId && r.kind === "reference-type"))
    .filter((r) => !!r)
    .map((r) => ({ id: r.id, name: r.name }));
  return types.length ? <BoundTypesLinks types={types} /> : null;
}

function BoundTypesLinks({ types }: { types: { id: string; name: string }[] }) {
  const { goTo } = useEditorNavigation();
  return (
    <p className="border-b border-default px-2 py-1 text-12 text-secondary" data-testid="bound-types-note">
      This table also holds the rows of{" "}
      {types.map((t, i) => (
        <span key={t.id}>
          {i ? ", " : ""}
          <button type="button" className="text-accent underline-offset-2 hover:underline" onClick={() => goTo(t.id)} title={`Open reference type ${t.name}`}>
            {t.name}
          </button>
        </span>
      ))}
      {types.length > 1 ? " (reference types bound to it)" : " (a reference type bound to it)"}: they come before the rows below, which may not repeat their
      codes.
    </p>
  );
}
