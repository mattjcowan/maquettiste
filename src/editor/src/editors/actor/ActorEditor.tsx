// The actor editor (phase-3-design.md 2.5 and 6.2): a small form tab (name, type, the stereotype, tag and category
// chips, the processes and gates that use it), Code generation and References. Actors are not in a domain.
import { useState } from "react";
import { useIndex, useElements } from "@/api/queries";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { References } from "@/inspector/Inspector";
import { TextField } from "@/inspector/fields";
import { indexLookup } from "@/model/index";
import { systemActorProblem, type ActorRow } from "@/model/actors";
import { ACTOR_TYPE_OPTION_LABELS, EDITOR_TAB_LABELS } from "@/model/labels";
import { actorUses, type ProcessDoc } from "@/model/process";
import { MemberOfField, MembersSection } from "./ActorMembers";
import { RelativeSection } from "./ActorRelative";
import { domIdOf, EditorLayout, MarkChips, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";

type Rec = Record<string, unknown>;

export function ActorEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "actor");
  if (!ctx) return <>{fallback}</>;
  return <ActorBody ctx={ctx} draft={draft} />;
}

/** The processes and gates that use an actor, one line each. */
export function useActorUses(actor: string): string[] {
  const index = useIndex();
  const ids = indexLookup(index.data)
    .ofKind("process")
    .map((p) => p.id);
  const docs = useElements(ids);
  return ids.flatMap((pid) => {
    const doc = docs.byId.get(pid);
    return doc ? actorUses(doc.json as unknown as ProcessDoc, actor) : [];
  });
}

export function ActorTypeField({ ctx, dom }: { ctx: Pick<EditorContext, "json" | "edit" | "flush"> & { id?: string }; dom: string }) {
  const index = useIndex();
  // Automatic (the system itself) is one actor per project: choosing it while another has it is refused, with the reason.
  const [refused, setRefused] = useState<string | null>(null);
  return (
    <Field label="Type" htmlFor={`${dom}-actor-type`}>
      <Select
        id={`${dom}-actor-type`}
        value={String((ctx.json as Rec).type ?? "person")}
        onChange={(e) => {
          const next = e.target.value;
          const problem = next === "system" ? systemActorProblem((index.data ?? []) as ActorRow[], ctx.id) : null;
          setRefused(problem);
          if (problem) return;
          ctx.edit((j) => void ((j as Rec).type = next));
          ctx.flush();
        }}
      >
        {Object.entries(ACTOR_TYPE_OPTION_LABELS).map(([value, label]) => (
          <option key={value} value={value}>
            {label}
          </option>
        ))}
      </Select>
      {refused ? (
        <p className="text-11 text-danger" role="alert" data-testid="actor-type-refused">
          {refused}
        </p>
      ) : null}
    </Field>
  );
}

export function ActorUsesList({ actor }: { actor: string }) {
  const uses = useActorUses(actor);
  return (
    <section className="flex flex-col gap-1" aria-label="Used by" data-testid="actor-uses">
      <SectionTitle>Used by</SectionTitle>
      {uses.length ? (
        <ul className="flex flex-col gap-0.5 text-12">
          {uses.map((u) => (
            <li key={u}>{u}</li>
          ))}
        </ul>
      ) : (
        <p className="text-12 text-secondary">No process uses this actor yet.</p>
      )}
    </section>
  );
}

function ActorBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const dom = domIdOf(ctx.id);
  const rec = ctx.json as Rec;
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex flex-col gap-2" data-testid="actor-controls">
          <div className="grid grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] items-end gap-2">
            <TextField
              id={`${dom}-name`}
              label="Name"
              value={String(rec.name ?? "")}
              invalid={ctx.diagnostics.some((d) => d.jsonPointer === "/name")}
              onChange={(v) => ctx.edit((j) => void ((j as Rec).name = v))}
              onBlur={ctx.flush}
            />
            <ActorTypeField ctx={ctx} dom={dom} />
          </div>
          {rec.relative === undefined ? <MemberOfField ctx={ctx} /> : null}
          <MarkChips {...ctx} />
        </div>
      }
      tabs={[
        {
          value: "general",
          label: "General",
          content: (
            <div className="flex flex-col gap-3">
              <RelativeSection ctx={ctx} />
              {rec.relative === undefined ? <MembersSection id={ctx.id} type={String(rec.type ?? "")} /> : null}
              <ActorUsesList actor={ctx.id} />
            </div>
          ),
        },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}
