// The routine editor: General (name, schema, comment and the marks), Definition (kind, language, deterministic, security, the
// result and what the routine depends on), Parameters (a grid), Body (one SQL editor per dialect), Code generation and
// References. Each committed field is one save, so one undo step.
import { useState } from "react";
import { CheckboxField } from "@/components/ui/checkbox";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { CommonFields, setOptional } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { routineBodyTemplate } from "@/explorer/databaseCreate";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { facetProblem, returnsShape, setFacet, setReturnsShape, setRowNativeType, setRowType, type ReturnsShape, type TypeFacet } from "./databaseDocs";
import { DependsOnField } from "./DependsOn";
import { DialectBodies } from "./DialectBodies";
import { CommentField, CommitInput, DatabaseLine, RoutineKindField, SchemaField } from "./fields";
import { TypedRowsGrid, TypeSelect, useDatabaseTypes } from "./TypedRowsGrid";

type Rec = Record<string, unknown>;

export const ROUTINE_EDITOR_TABS = { general: "General", definition: "Definition", parameters: "Parameters", body: "Body" } as const;

const RETURNS_LABELS: Record<ReturnsShape, string> = { none: "Nothing", value: "A single value", table: "A table" };

export function RoutineEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "routine");
  if (!ctx) return <>{fallback}</>;
  return <RoutineBody ctx={ctx} draft={draft} />;
}

function RoutineBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const doc = ctx.json as unknown as Rec;
  const routineKind = doc.routineKind === "procedure" ? "procedure" : "function";
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<DatabaseLine json={ctx.json} />}
      tabs={[
        {
          value: "general",
          label: ROUTINE_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="routine-editor-general">
              <CommonFields {...form} inEditorHeader />
              <SchemaField {...form} />
              <CommentField {...form} />
            </div>
          ),
        },
        { value: "definition", label: ROUTINE_EDITOR_TABS.definition, content: <DefinitionTab ctx={ctx} /> },
        {
          value: "parameters",
          label: ROUTINE_EDITOR_TABS.parameters,
          content: (
            <TypedRowsGrid
              ctx={ctx}
              path={["parameters"]}
              noun="parameter"
              prefix="routine-parameters"
              parameter
              base="param"
              newType="int32"
              intro="The routine's parameters, in order: a built-in type or a database type of this database, or a native type that replaces it."
              empty={{ title: "No parameters", text: "The routine takes no arguments." }}
            />
          ),
        },
        {
          value: "body",
          label: ROUTINE_EDITOR_TABS.body,
          content: (
            <DialectBodies
              ctx={ctx}
              member="body"
              prefix="routine"
              noun="Routine"
              intro="What follows the signature, per dialect; a body for any dialect (*) serves the dialects without one of their own."
              template={(d) => routineBodyTemplate(d, routineKind)}
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

function DefinitionTab({ ctx }: { ctx: EditorContext }) {
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const id = form.id;
  const doc = ctx.json as unknown as Rec;
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  const shape = returnsShape(doc);
  return (
    <div className="flex max-w-3xl flex-col gap-2" data-testid="routine-definition">
      <div className="grid grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] gap-2">
        <RoutineKindField {...form} />
        <Field label="Language" htmlFor={`${id}-language`} hint="Empty: the dialect's own procedural language.">
          <CommitInput
            id={`${id}-language`}
            mono
            value={String(doc.language ?? "")}
            placeholder="the dialect's own"
            onCommit={(v) => update((j) => setOptional(j, "language", v.trim()))}
          />
        </Field>
        <Field label="Security" htmlFor={`${id}-security`} hint="Whose rights the routine runs with.">
          <Select
            id={`${id}-security`}
            value={String(doc.security ?? "invoker")}
            onChange={(e) => update((j) => setOptional(j, "security", e.target.value === "invoker" ? undefined : e.target.value))}
          >
            <option value="invoker">The caller&apos;s (invoker)</option>
            <option value="definer">Its owner&apos;s (definer)</option>
          </Select>
        </Field>
      </div>
      <CheckboxField
        id={`${id}-deterministic`}
        label="Deterministic (the same arguments always give the same result)"
        checked={doc.deterministic === true}
        onChange={(on) => update((j) => setOptional(j, "deterministic", on ? true : undefined))}
      />
      <SectionTitle>Returns</SectionTitle>
      <Field label="Result" htmlFor={`${id}-returns`}>
        <Select id={`${id}-returns`} className="max-w-xs" value={shape} onChange={(e) => update((j) => setReturnsShape(j, e.target.value as ReturnsShape))}>
          {(Object.keys(RETURNS_LABELS) as ReturnsShape[]).map((s) => (
            <option key={s} value={s}>
              {RETURNS_LABELS[s]}
            </option>
          ))}
        </Select>
      </Field>
      {shape === "value" ? <ReturnsValue ctx={ctx} /> : null}
      {shape === "table" ? (
        <TypedRowsGrid
          ctx={ctx}
          path={["returns", "table"]}
          noun="column"
          prefix="routine-returns"
          nullable
          keepOne
          base="column"
          intro="The columns of the table the routine returns."
        />
      ) : null}
      <SectionTitle>Order</SectionTitle>
      <DependsOnField ctx={ctx} id={id} />
    </div>
  );
}

const FACETS: TypeFacet[] = ["length", "precision", "scale"];

/** A single-value result: its type, facets and native type. */
function ReturnsValue({ ctx }: { ctx: EditorContext }) {
  const id = domIdOf(ctx.id);
  const doc = ctx.json as unknown as Rec;
  const returns = (doc.returns as Rec | undefined) ?? {};
  const types = useDatabaseTypes(String(doc.database ?? ""));
  const [problem, setProblem] = useState<string | null>(null);
  const change = (apply: (row: Rec) => boolean, refused: string) => {
    if (!apply(structuredClone(returns))) {
      setProblem(refused);
      return;
    }
    setProblem(null);
    ctx.edit((j) => void apply((j as unknown as Rec).returns as Rec));
    ctx.flush();
  };
  return (
    <div className="flex flex-col gap-2" data-testid="routine-returns-value">
      <div className="grid grid-cols-[12rem_repeat(3,5rem)_12rem] gap-2">
        <Field label="Type" htmlFor={`${id}-returns-type`}>
          <TypeSelect
            id={`${id}-returns-type`}
            value={typeof returns.type === "string" ? returns.type : ""}
            types={types}
            allowNative={typeof returns.nativeType === "string"}
            onChange={(v) => change((row) => setRowType(row, v), "The result needs a type or a native type.")}
          />
        </Field>
        {FACETS.map((f) => (
          <Field key={f} label={`${f[0].toUpperCase()}${f.slice(1)}`} htmlFor={`${id}-returns-${f}`}>
            <CommitInput
              id={`${id}-returns-${f}`}
              mono
              value={typeof returns[f] === "number" ? String(returns[f]) : ""}
              onCommit={(v) => change((row) => setFacet(row, f, v), `${f[0].toUpperCase()}${f.slice(1)}: ${facetProblem(f, v) ?? ""}`)}
            />
          </Field>
        ))}
        <Field label="Native type" htmlFor={`${id}-returns-native`}>
          <CommitInput
            id={`${id}-returns-native`}
            mono
            value={String(returns.nativeType ?? "")}
            placeholder="from the type"
            onCommit={(v) => change((row) => setRowNativeType(row, v), "The result needs a type or a native type.")}
          />
        </Field>
      </div>
      {problem ? (
        <p role="alert" className="text-12 text-danger">
          {problem}
        </p>
      ) : null}
    </div>
  );
}
