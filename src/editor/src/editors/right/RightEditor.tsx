// The named right's editor (rights-design.md section 10): the right's fields without states (Name, Allows or denies, Who,
// Except, Does, Links, When, Why), a Used by list and References. Its When box previews against a lifecycle that uses the right,
// picked beside the box; with no use the text is kept as written and the box says the right needs a use.
import { useState } from "react";
import { useElements, useIndex } from "@/api/queries";
import { Field, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { References } from "@/inspector/Inspector";
import { ChipsEditor, TextField } from "@/inspector/fields";
import { indexLookup } from "@/model/index";
import { EDITOR_TAB_LABELS, RIGHT_LABELS as L } from "@/model/labels";
import { treeText } from "@/model/effects";
import type { Json, ProcessDoc } from "@/model/process";
import { EVERYONE, takesAttributes } from "@/model/rights";
import { subjectsOf } from "@/model/process";
import { verbGroups } from "@/model/verbList";
import { useVerbs } from "@/verbs/useVerbs";
import { useSetContext } from "../process/SetsEditor";
import { useActors } from "../process/shared";
import { useObjectFields } from "../actor/useObjectFields";
import { ValueBox } from "../ValueBox";
import { domIdOf, EditorLayout, MarkChips, useEditorContext, type EditorContext } from "../EditorFrame";

type Rec = Record<string, unknown>;
const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);

export function RightEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "right");
  if (!ctx) return <>{fallback}</>;
  return <RightBody ctx={ctx} draft={draft} />;
}

/** The lifecycles that use a named right, with the states each uses it in. */
export function useRightUses(right: string): { id: string; name: string; states: string[] }[] {
  const index = useIndex();
  const ids = indexLookup(index.data)
    .ofKind("process")
    .filter((p) => p.use === "lifecycle" && (p.rights ?? 0) > 0)
    .map((p) => p.id);
  const docs = useElements(ids);
  return ids.flatMap((pid) => {
    const doc = docs.byId.get(pid)?.json as unknown as ProcessDoc | undefined;
    const use = doc?.rights?.find((r) => r.right === right);
    return doc && use ? [{ id: pid, name: doc.name, states: strings(use.states) }] : [];
  });
}

function RightBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const dom = domIdOf(ctx.id);
  const rec = ctx.json as Rec;
  const { actors } = useActors();
  const verbs = useVerbs();
  const uses = useRightUses(ctx.id);
  const [lifecycle, setLifecycle] = useState<string | null>(null);
  const at = uses.find((u) => u.id === lifecycle) ?? uses[0];
  const lifecycleDocs = useElements(at ? [at.id] : []);
  const process = lifecycleDocs.byId.get(at?.id ?? "")?.json as unknown as ProcessDoc | undefined;
  // The names a stored condition prints with: those of the lifecycle it is previewed in.
  const names = useSetContext(process ?? ({ id: ctx.id, name: "", states: [] } as unknown as ProcessDoc)).names;
  const set = (update: (j: Rec) => void) => {
    ctx.edit((j) => void update(j as Rec));
    ctx.flush();
  };
  const setList = (key: string, values: string[]) =>
    set((j) => {
      if (values.length) j[key] = values;
      else delete j[key];
    });
  // Attributes are the ones every subject of the lifecycle it is previewed in holds.
  const objectFields = useObjectFields(process ? subjectsOf(process).flatMap((s) => s.entity ?? []) : []);
  const attributeOptions = objectFields.fields.filter((f) => f.kind === "attribute").map((f) => ({ value: f.id, label: f.name }));
  const actorOptions = actors.map((a) => ({ value: a.id, label: a.name }));
  const verbOptions = verbGroups("right", verbs.own, verbs.hidden).flatMap((g) => g.options.map((o) => ({ value: o.key, label: o.label })));
  const condition = rec.condition as Json | undefined;
  const text = condition ? treeText(condition, names) : "";
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex flex-col gap-2" data-testid="right-controls">
          <div className="grid grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] items-end gap-2">
            <TextField
              id={`${dom}-name`}
              label={L.columns.name}
              value={String(rec.name ?? "")}
              invalid={ctx.diagnostics.some((d) => d.jsonPointer === "/name")}
              onChange={(v) => ctx.edit((j) => void ((j as Rec).name = v))}
              onBlur={ctx.flush}
            />
            <Field label={L.columns.access} htmlFor={`${dom}-access`} hint={L.tips.access}>
              <Select id={`${dom}-access`} value={rec.access === "allow" ? "allow" : "deny"} onChange={(e) => set((j) => void (j.access = e.target.value))}>
                <option value="allow">{L.allow}</option>
                <option value="deny">{L.deny}</option>
              </Select>
            </Field>
          </div>
          <MarkChips {...ctx} />
        </div>
      }
      tabs={[
        {
          value: "general",
          label: "General",
          content: (
            <div className="flex flex-col gap-3">
              <div className="grid grid-cols-[repeat(auto-fill,minmax(14rem,1fr))] gap-2">
                <ChipsEditor
                  label={L.columns.who}
                  whenNone={EVERYONE}
                  values={strings(rec.actors)}
                  options={actorOptions}
                  allowFree={false}
                  onChange={(v) => setList("actors", v)}
                />
                <ChipsEditor
                  label={L.columns.except}
                  values={strings(rec.except)}
                  options={actorOptions}
                  allowFree={false}
                  onChange={(v) => setList("except", v)}
                />
                <ChipsEditor
                  label={L.columns.does}
                  values={strings(rec.verbs)}
                  options={verbOptions}
                  allowFree={false}
                  onChange={(v) => v.length && setList("verbs", v)}
                />
                {takesAttributes(strings(rec.verbs)) ? (
                  <ChipsEditor
                    label={L.columns.attributes}
                    whenNone="Every attribute"
                    values={strings(rec.attributes)}
                    options={attributeOptions}
                    allowFree={false}
                    onChange={(v) => setList("attributes", v)}
                  />
                ) : null}
              </div>
              <label className="flex items-center gap-2 text-12" title={L.tips.whileFrozen}>
                <input
                  type="checkbox"
                  checked={rec.whileFrozen === true}
                  onChange={(e) =>
                    set((j) => {
                      if (e.target.checked) j.whileFrozen = true;
                      else delete j.whileFrozen;
                    })
                  }
                  data-testid="right-while-frozen"
                />
                {L.columns.whileFrozen}
              </label>
              <section className="flex flex-col gap-1" aria-label={L.columns.when}>
                <SectionTitle>{L.columns.when}</SectionTitle>
                <div className="flex items-start gap-2">
                  <ValueBox
                    key={at?.id ?? "none"}
                    id={`${dom}-when`}
                    label={L.columns.when}
                    initial={text}
                    placeholder={L.tips.when}
                    request={(t) =>
                      t === undefined
                        ? condition && at
                          ? { element: ctx.id, lifecycle: at.id }
                          : null
                        : t.trim() && at
                          ? { element: ctx.id, site: "right", text: t, lifecycle: at.id }
                          : null
                    }
                    onChange={() => undefined}
                    onCommit={(t) =>
                      set((j) => {
                        if (t.trim()) j.condition = { text: t.trim() };
                        else delete j.condition;
                      })
                    }
                  />
                  <Select
                    aria-label={L.previewIn}
                    title={L.previewIn}
                    className="h-6 w-44 shrink-0"
                    data-testid="right-preview-lifecycle"
                    value={at?.id ?? ""}
                    disabled={!uses.length}
                    onChange={(e) => setLifecycle(e.target.value)}
                  >
                    {uses.length ? null : <option value="">{L.previewNone}</option>}
                    {uses.map((u) => (
                      <option key={u.id} value={u.id}>
                        {u.name}
                      </option>
                    ))}
                  </Select>
                </div>
                {!uses.length && text ? (
                  <p className="text-11 text-secondary" data-testid="right-needs-use">
                    {L.noUse}
                  </p>
                ) : null}
              </section>
              <section className="flex flex-col gap-1" aria-label={L.usedBy} data-testid="right-uses">
                <SectionTitle>{L.usedBy}</SectionTitle>
                {uses.length ? (
                  <ul className="flex flex-col gap-0.5 text-12">
                    {uses.map((u) => (
                      <li key={u.id}>{u.name}</li>
                    ))}
                  </ul>
                ) : (
                  <p className="text-12 text-secondary">{L.notUsed}</p>
                )}
              </section>
            </div>
          ),
        },
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}
