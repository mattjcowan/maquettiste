// The inspector's Attributes tab for an entity: a compact read-only list (name, type, required), one ROW_H row per
// attribute. The entity's grid is edited in the entity editor; "Open editor" opens it on its Attributes tab.
import type { EntityDoc, ModelJson } from "@/api/types";
import { useIndex } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { ROW_H } from "@/design/density";
import { setView } from "@/editors/tabs";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { indexLookup } from "@/model/index";
import { TYPE_KINDS } from "@/model/model";
import { attributeTypeLabel } from "./AttributeGrid";
import { useVocabularies } from "./fields";

export function EntityAttributeList({ id, json }: { id: string; json: ModelJson }) {
  const entity = json as unknown as EntityDoc;
  const attributes = entity.attributes ?? [];
  const keyIds = new Set(entity.key?.attributes ?? []);
  const vocab = useVocabularies("entity");
  const typeOptions = TYPE_KINDS.flatMap((k) => vocab.lookup.ofKind(k));
  const index = useIndex();
  const { store } = useServices();
  const { openEditor } = useEditorNavigation();
  const open = () => {
    const summary = indexLookup(index.data).byId.get(id);
    if (!summary) return;
    store.getState().updateEditors((e) => setView(e, "entity", "attributes"));
    openEditor(summary, true);
  };
  return (
    <div className="flex flex-col gap-1" data-testid="inspector-attribute-list">
      <SectionTitle
        actions={
          <Button size="sm" variant="link" className="h-6 px-0" onClick={open}>
            Open editor
          </Button>
        }
      >
        {attributes.length === 1 ? "1 attribute" : `${attributes.length} attributes`}
      </SectionTitle>
      {attributes.length ? (
        <ul className="flex flex-col text-12" aria-label={`Attributes of ${entity.name}`}>
          {attributes.map((a) => (
            <li key={a.id} className="flex items-center gap-2 border-b border-default px-1" style={{ height: ROW_H }}>
              <span className="min-w-0 flex-1 truncate">
                {a.name}
                {keyIds.has(a.id) ? <span className="ml-1 text-11 text-secondary">key</span> : null}
              </span>
              <span className="max-w-[50%] truncate font-mono text-11 text-secondary">{attributeTypeLabel(a, typeOptions)}</span>
              <span className="w-3 text-center text-danger" title={a.required ? "Required" : undefined}>
                {a.required ? <span aria-label="required">*</span> : null}
              </span>
            </li>
          ))}
        </ul>
      ) : (
        <EmptyState title="No attributes yet">Add them in the entity editor.</EmptyState>
      )}
    </div>
  );
}
