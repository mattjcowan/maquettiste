// The Seed data tab of the entity and relationship editors: the Rows grid of the Reference data screen over the
// element's seeds (add, edit and delete rows by keyboard, CSV import with a preview, CSV export), end cells picked
// from the far entity's rows. With no seed yet, New seed creates one on request: nothing is created by itself.
import { useState } from "react";
import { Plus } from "lucide-react";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { SeedGrid } from "@/workspaces/reference-data/RowsGrid";
import { createTargetSeed, useSeedTarget } from "@/workspaces/reference-data/seedTargets";

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
      <EmptyState title="No seed data">
        <p>Seed data is the rows {target.name} starts with. It is kept with the model; how it is written out is up to the generator.</p>
        <Button className="mt-2" size="sm" disabled={creating || !columns} onClick={() => void create()} data-testid="new-seed">
          <Plus /> New seed
        </Button>
      </EmptyState>
    );
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="seed-data-grid">
      <SeedGrid key={id} name={target.name} columns={columns} seeds={seeds} keepStoredColumns endOptions={endOptions} createSeed={create} />
    </div>
  );
}
