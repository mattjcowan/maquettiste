// The SQL object editor: General (name, schema and the marks), Definition (what the object is, when it runs and what it
// depends on), Body (the statements, one SQL editor per dialect), Code generation and References. Each committed field is one
// save, so one undo step.
import { CommonFields } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { SQL_OBJECT_BODY_TEMPLATE } from "@/explorer/databaseCreate";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { DependsOnField } from "./DependsOn";
import { DialectBodies } from "./DialectBodies";
import { DatabaseLine, ObjectKindField, PhaseField, SchemaField } from "./fields";

export const SQL_OBJECT_EDITOR_TABS = { general: "General", definition: "Definition", body: "Body" } as const;

export function SqlObjectEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "sql-object");
  if (!ctx) return <>{fallback}</>;
  return <SqlObjectBody ctx={ctx} draft={draft} />;
}

function SqlObjectBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
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
          label: SQL_OBJECT_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="sql-object-editor-general">
              <CommonFields {...form} inEditorHeader />
              <SchemaField {...form} />
            </div>
          ),
        },
        {
          value: "definition",
          label: SQL_OBJECT_EDITOR_TABS.definition,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="sql-object-definition">
              <div className="grid grid-cols-2 gap-2">
                <ObjectKindField {...form} />
                <PhaseField {...form} />
              </div>
              <DependsOnField ctx={ctx} id={form.id} />
            </div>
          ),
        },
        {
          value: "body",
          label: SQL_OBJECT_EDITOR_TABS.body,
          content: (
            <DialectBodies
              ctx={ctx}
              member="body"
              prefix="sql-object"
              noun="SQL object"
              intro="The statements that create the object, per dialect, run as written; statements for any dialect (*) serve the dialects without their own."
              template={() => SQL_OBJECT_BODY_TEMPLATE}
              required
            />
          ),
        },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}
