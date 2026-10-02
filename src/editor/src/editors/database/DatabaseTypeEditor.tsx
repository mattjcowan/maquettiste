// The database type editor: General (name, schema, comment and the marks), Definition (the type kind, then what that kind
// uses: a domain's base type, facets and check; a composite's fields; an enum's labels; a range's subtype; and the native name
// columns write), Dialects (a definition per dialect that replaces the structured form), Code generation and References. Each
// committed field is one save, so one undo step.
import { useState } from "react";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Select } from "@/components/ui/input";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { CommonFields, setOptional } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { BUILTIN_TYPES } from "@/model/model";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { useDatabaseView } from "@/api/queries";
import { addMember, definitionTemplate, facetProblem, memberProblem, moveRow, setFacet, type TypeFacet } from "./databaseDocs";
import { DialectBodies } from "./DialectBodies";
import { CommentField, CommitInput, DatabaseLine, SchemaField, TypeKindField } from "./fields";
import { TypedRowsGrid } from "./TypedRowsGrid";

type Rec = Record<string, unknown>;

export const DATABASE_TYPE_EDITOR_TABS = { general: "General", definition: "Definition", dialects: "Dialects" } as const;

export function DatabaseTypeEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "database-type");
  if (!ctx) return <>{fallback}</>;
  return <DatabaseTypeBody ctx={ctx} draft={draft} />;
}

function DatabaseTypeBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const doc = ctx.json as unknown as Rec;
  // A dialect's definition starts from the structured form, with the native types the database's dialect gives it.
  const resolved = useDatabaseView(String(doc.database ?? "")).data?.view?.types?.find((t) => t.id === ctx.id);
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<DatabaseLine json={ctx.json} />}
      tabs={[
        {
          value: "general",
          label: DATABASE_TYPE_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="database-type-editor-general">
              <CommonFields {...form} inEditorHeader />
              <SchemaField {...form} />
              <CommentField {...form} />
            </div>
          ),
        },
        { value: "definition", label: DATABASE_TYPE_EDITOR_TABS.definition, content: <DefinitionTab ctx={ctx} /> },
        {
          value: "dialects",
          label: DATABASE_TYPE_EDITOR_TABS.dialects,
          content: (
            <DialectBodies
              ctx={ctx}
              member="definition"
              prefix="database-type"
              noun="Database type"
              intro="Optional: the definition written after the type's name, per dialect, when the structured form does not say it; it replaces the structured form for that dialect."
              template={() => definitionTemplate(doc, resolved)}
              required={false}
              empty={{ title: "No dialect definitions", text: "The scripts build the type from its Definition tab." }}
            />
          ),
        },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}

const FACETS: TypeFacet[] = ["length", "precision", "scale"];

function DefinitionTab({ ctx }: { ctx: EditorContext }) {
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const id = form.id;
  const doc = ctx.json as unknown as Rec;
  const typeKind = String(doc.typeKind ?? "domain");
  const [problem, setProblem] = useState<string | null>(null);
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  const builtinSelect = (member: "base" | "subtype", label: string) => (
    <Field label={label} htmlFor={`${id}-${member}`}>
      <Select
        id={`${id}-${member}`}
        className="font-mono"
        value={String(doc[member] ?? "")}
        onChange={(e) => update((j) => setOptional(j, member, e.target.value))}
      >
        {doc[member] ? null : <option value="">(none)</option>}
        {BUILTIN_TYPES.map((t) => (
          <option key={t} value={t}>
            {t}
          </option>
        ))}
      </Select>
    </Field>
  );
  return (
    <div className="flex max-w-3xl flex-col gap-2" data-testid="database-type-definition" data-type-kind={typeKind}>
      <div className="max-w-sm">
        <TypeKindField {...form} />
      </div>
      {typeKind === "domain" ? (
        <section className="flex flex-col gap-2" aria-label="Domain" data-testid="database-type-domain">
          <div className="grid grid-cols-[12rem_repeat(3,5rem)] gap-2">
            {builtinSelect("base", "Base type")}
            {FACETS.map((f) => (
              <Field key={f} label={`${f[0].toUpperCase()}${f.slice(1)}`} htmlFor={`${id}-${f}`}>
                <CommitInput
                  id={`${id}-${f}`}
                  mono
                  value={typeof doc[f] === "number" ? String(doc[f]) : ""}
                  onCommit={(v) => {
                    const why = facetProblem(f, v);
                    setProblem(why ? `${f[0].toUpperCase()}${f.slice(1)}: ${why}` : null);
                    if (!why) update((j) => void setFacet(j, f, v));
                  }}
                />
              </Field>
            ))}
          </div>
          <Field label="Check" htmlFor={`${id}-check`} hint="A CHECK expression over VALUE, such as VALUE like '%@%'. Empty: none.">
            <CommitInput id={`${id}-check`} mono value={String(doc.check ?? "")} onCommit={(v) => update((j) => setOptional(j, "check", v.trim()))} />
          </Field>
        </section>
      ) : null}
      {typeKind === "composite" ? (
        <TypedRowsGrid
          ctx={ctx}
          path={["fields"]}
          noun="field"
          prefix="database-type-fields"
          base="field"
          self={ctx.id}
          intro="The fields of the composite, in order: a built-in type or another database type of this database."
          empty={{ title: "No fields", text: "A composite needs a field, or a definition per dialect, to be created." }}
        />
      ) : null}
      {typeKind === "enum" ? <MembersList ctx={ctx} /> : null}
      {typeKind === "range" ? <div className="max-w-xs">{builtinSelect("subtype", "Subtype")}</div> : null}
      {problem ? (
        <p role="alert" className="text-12 text-danger">
          {problem}
        </p>
      ) : null}
      <SectionTitle>Columns</SectionTitle>
      <Field
        label="Native name"
        htmlFor={`${id}-native-name`}
        hint="What columns and parameters of this type write; empty: the type's own name, with its schema. A column uses the type through its native type (the type's name)."
      >
        <CommitInput
          id={`${id}-native-name`}
          mono
          value={String(doc.nativeName ?? "")}
          placeholder="the type's name"
          onCommit={(v) => update((j) => setOptional(j, "nativeName", v.trim()))}
        />
      </Field>
    </div>
  );
}

/** An enum type's labels, in order: renamed in place, moved, added and deleted. */
function MembersList({ ctx }: { ctx: EditorContext }) {
  const doc = ctx.json as unknown as Rec;
  const members = Array.isArray(doc.members) ? (doc.members as string[]) : [];
  const [problem, setProblem] = useState<string | null>(null);
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  return (
    <section className="flex flex-col gap-2" aria-label="Labels" data-testid="database-type-members">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">The labels of the enum, in order; each appears once.</p>
        <Button size="sm" variant="ghost" onClick={() => update(addMember)} data-testid="database-type-add-member">
          <Plus /> Add label
        </Button>
      </div>
      {members.length ? (
        <ul className="flex max-w-md flex-col">
          {members.map((m, i) => (
            <li key={`${i}:${m}`} className="flex h-[var(--mq-row-h)] items-center gap-1" data-testid={`database-type-member-${m}`}>
              <CommitInput
                label={`Label ${i + 1}`}
                mono
                className="h-6 text-12"
                value={m}
                onCommit={(v) => {
                  const why = memberProblem(members, i, v);
                  setProblem(why);
                  if (!why) update((j) => void ((j.members as string[])[i] = v.trim()));
                }}
              />
              <Button
                size="icon-row"
                variant="ghost"
                label={`Move ${m} up`}
                disabled={i === 0}
                onClick={() => update((j) => void moveRow(j, "members", i, -1))}
              >
                <ArrowUp />
              </Button>
              <Button
                size="icon-row"
                variant="ghost"
                label={`Move ${m} down`}
                disabled={i === members.length - 1}
                onClick={() => update((j) => void moveRow(j, "members", i, 1))}
              >
                <ArrowDown />
              </Button>
              <Button
                size="icon-row"
                variant="ghost"
                label={`Delete ${m}`}
                onClick={() =>
                  update((j) => {
                    const next = (j.members as string[]).filter((_, k) => k !== i);
                    if (next.length) j.members = next;
                    else delete j.members;
                  })
                }
              >
                <Trash2 />
              </Button>
            </li>
          ))}
        </ul>
      ) : (
        <EmptyState title="No labels">An enum needs a label, or a definition per dialect, to be created.</EmptyState>
      )}
      {problem ? (
        <p role="alert" className="text-12 text-danger">
          {problem}
        </p>
      ) : null}
    </section>
  );
}
