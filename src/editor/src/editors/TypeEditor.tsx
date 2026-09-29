// The enum, value object and custom type editors (explorer-redesign.md 3.6), on the entity editor's frame: name,
// domain and the mark chips on top; the members, fields or definition, Code generation (when an extension schema
// applies) and References as tabs.
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { References } from "@/inspector/Inspector";
import { AttributesOnlyFields, EnumFields, ScalarFields } from "@/inspector/fields";
import { domIdOf, EditorLayout, MarkChips, NameAndDomain, useCodeGenerationTab, useEditorContext, type EditorContext } from "./EditorFrame";

type TypeKind = "enum" | "value-object" | "scalar-type";

export function TypeEditor({ id, kind }: { id: string; kind: TypeKind }) {
  const { ctx, fallback, draft } = useEditorContext(id, kind);
  if (!ctx) return <>{fallback}</>;
  return <TypeBody ctx={ctx} draft={draft} />;
}

function TypeBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const main =
    ctx.kind === "enum"
      ? { value: "members", label: EDITOR_TAB_LABELS.members, content: <EnumFields {...form} /> }
      : ctx.kind === "value-object"
        ? { value: "attributes", label: EDITOR_TAB_LABELS.attributes, content: <AttributesOnlyFields {...form} /> }
        : { value: "definition", label: EDITOR_TAB_LABELS.definition, content: <ScalarFields {...form} /> };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex flex-col gap-3">
          <NameAndDomain {...ctx} />
          <MarkChips {...ctx} />
        </div>
      }
      tabs={[main, codeGeneration, { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> }]}
    />
  );
}
