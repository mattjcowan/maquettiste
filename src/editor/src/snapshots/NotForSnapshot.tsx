// What a screen shows instead of itself while a snapshot is shown, when the API does not serve its reads for a snapshot.
import { EmptyState } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { useAsOfNavigation } from "./state";

export function NotForSnapshot({ what, compact = false }: { what: string; compact?: boolean }) {
  const { back } = useAsOfNavigation();
  // A side panel (the Generate explorer) says it in one line; the screen beside it says the rest.
  if (compact) return <p className="p-2 text-12 text-secondary">{what} is not available for a snapshot.</p>;
  return (
    <div className="h-full" data-testid="not-for-snapshot">
      <EmptyState title={`${what} is not available for a snapshot`}>
        <p className="text-secondary">It works on the working model only. The model's elements, diagrams and databases can be viewed as of the snapshot.</p>
        <Button size="sm" className="mt-1" onClick={back}>
          Back to working
        </Button>
      </EmptyState>
    </div>
  );
}
