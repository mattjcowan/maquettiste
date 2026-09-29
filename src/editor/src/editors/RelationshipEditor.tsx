// The relationship editor (explorer-redesign.md 3.6): name and domain, the ends (entity, role, cardinality, delete
// behaviour) and the mark chips on top; tabs Attributes, Code generation (when an extension schema applies) and
// References. On the same frame as the entity editor.
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { References } from "@/inspector/Inspector";
import { AttributesOnlyFields, RelationFields } from "@/inspector/fields";
import { domIdOf, EditorLayout, MarkChips, NameAndDomain, useCodeGenerationTab, useEditorContext, type EditorContext } from "./EditorFrame";

export function RelationshipEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "relation");
  if (!ctx) return <>{fallback}</>;
  return <RelationshipBody ctx={ctx} draft={draft} />;
}

function RelationshipBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  // The shared forms use `id` only for their field ids: give them the editor's prefix.
  const form = { ...ctx, id: domIdOf(ctx.id) };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex flex-col gap-3">
          <NameAndDomain {...ctx} />
          <RelationFields {...form} withAttributes={false} />
          <MarkChips {...ctx} />
        </div>
      }
      tabs={[
        { value: "attributes", label: EDITOR_TAB_LABELS.attributes, content: <AttributesOnlyFields {...form} /> },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}
