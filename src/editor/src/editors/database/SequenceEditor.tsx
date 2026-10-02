// The sequence editor: General (name, schema and the marks), Definition (type, start, increment, minimum, maximum, cycle and
// cache), Code generation and References. Each committed field is one save, so one undo step.
import { CommonFields } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { DatabaseLine, SchemaField, SequenceFields } from "./fields";

export const SEQUENCE_EDITOR_TABS = { general: "General", definition: "Definition" } as const;

export function SequenceEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "sequence");
  if (!ctx) return <>{fallback}</>;
  return <SequenceBody ctx={ctx} draft={draft} />;
}

function SequenceBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<DatabaseLine json={ctx.json} />}
      tabs={[
        {
          value: "general",
          label: SEQUENCE_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="sequence-editor-general">
              <CommonFields {...form} inEditorHeader />
              <SchemaField {...form} />
            </div>
          ),
        },
        {
          value: "definition",
          label: SEQUENCE_EDITOR_TABS.definition,
          content: (
            <div className="max-w-2xl">
              <SequenceFields {...form} />
            </div>
          ),
        },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}
