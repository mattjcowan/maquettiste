// The inspector header's "Open editor": every kind with an element editor has it, in the same place. An entity's opens
// on its Attributes tab.
import type { ElementSummary } from "@/api/types";
import { Button } from "@/components/ui/button";
import { useEditorNavigation } from "@/app/navigation";
import { useServices } from "@/app/context";
import { setView } from "@/editors/tabs";

export function OpenEditorButton({ summary }: { summary: ElementSummary }) {
  const { store } = useServices();
  const { openEditor } = useEditorNavigation();
  return (
    <Button
      size="sm"
      variant="link"
      className="h-6 shrink-0 px-0"
      data-testid="inspector-open-editor"
      onClick={() => {
        if (summary.kind === "entity") store.getState().updateEditors((e) => setView(e, "entity", "attributes"));
        openEditor(summary, true);
      }}
    >
      Open editor
    </Button>
  );
}
