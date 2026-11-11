// The operation editor (next-phase-2-design.md section 4.1, first cut): General (the common fields, with the subject, level
// and effect read-only), JSON for the rest of the document (input, outcomes, errors, fires, requires), Code generation and
// References. An operation screen with grids and pickers is a later follow-up.
import { useIndex } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { Field, Input } from "@/components/ui/input";
import { CommonFields } from "@/inspector/fields";
import { useProblems } from "../process/shared";
import { FiresSection } from "./FiresSection";
import { VerbPicker } from "@/verbs/VerbsSettings";
import { JsonTab, References } from "@/inspector/Inspector";
import { indexLookup } from "@/model/index";
import { displayName } from "@/model/model";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";

export const OPERATION_EDITOR_TABS = { general: "General", json: "JSON" } as const;

type Rec = Record<string, unknown>;

export function OperationEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "operation");
  if (!ctx) return <>{fallback}</>;
  return <OperationBody ctx={ctx} draft={draft} />;
}

/** The subject's name, the level and the effect, as the operation file states them (the schema's defaults when absent). */
export function operationFacts(json: ModelJson, subjectName: string | undefined): { subject: string; level: string; effect: string } {
  const rec = json as Rec;
  const hasSubject = typeof rec.subject === "string" && rec.subject !== "";
  return {
    subject: hasSubject ? (subjectName ?? String(rec.subject)) : "None",
    level: hasSubject ? (rec.level === "collection" ? "collection" : "record") : "",
    effect: rec.effect === "change" ? "change" : "read",
  };
}

function OperationBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const index = useIndex();
  const subjectId = (ctx.json as Rec).subject;
  const subjectRow = typeof subjectId === "string" ? indexLookup(index.data).byId.get(subjectId) : undefined;
  const facts = operationFacts(ctx.json, subjectRow ? displayName(subjectRow) : undefined);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const problems = useProblems(ctx.id, draft?.status === "invalid" ? ctx.diagnostics : []);
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <span className="text-12 text-secondary" data-testid="operation-facts">
          {[facts.subject === "None" ? "No subject" : `On ${facts.subject}`, facts.level, facts.effect].filter(Boolean).join(" · ")}
        </span>
      }
      tabs={[
        {
          value: "general",
          label: OPERATION_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="operation-editor-general">
              <CommonFields {...form} inEditorHeader />
              <Field label="Subject" htmlFor={`${form.id}-subject`} hint="Change it in the JSON tab.">
                <Input id={`${form.id}-subject`} value={facts.subject} readOnly />
              </Field>
              {facts.level ? (
                <Field label="Level" htmlFor={`${form.id}-level`}>
                  <Input id={`${form.id}-level`} value={facts.level} readOnly />
                </Field>
              ) : null}
              <Field label="Effect" htmlFor={`${form.id}-effect`}>
                <Input id={`${form.id}-effect`} value={facts.effect} readOnly />
              </Field>
              <Field label="Verb" htmlFor={`${form.id}-verb`} hint="What the operation does.">
                <VerbPicker
                  place="operation"
                  id={`${form.id}-verb`}
                  value={typeof (ctx.json as Rec).verb === "string" ? ((ctx.json as Rec).verb as string) : undefined}
                  onChange={(key) =>
                    ctx.edit((j) => {
                      if (key) (j as Rec).verb = key;
                      else delete (j as Rec).verb;
                    })
                  }
                />
              </Field>
              <FiresSection
                operation={ctx.id}
                json={ctx.json as Rec}
                subject={typeof subjectId === "string" ? subjectId : undefined}
                problems={problems}
                edit={(update) => ctx.edit((j) => update(j as Rec))}
              />
            </div>
          ),
        },
        {
          value: "json",
          label: OPERATION_EDITOR_TABS.json,
          fill: true,
          content: <JsonTab json={ctx.json} onChange={(next: ModelJson) => ctx.edit(() => next)} />,
        },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}
