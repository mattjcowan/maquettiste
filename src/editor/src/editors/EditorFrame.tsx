// The frame every element editor shares (explorer-redesign.md 3.6): a header with the name, the save status, the
// display names and the description, a band of top controls, and sub-tabs. The sub-tab shown is kept per kind in the
// editor store, so moving to another element of the kind keeps it.
import { TranslationsSection } from "@/l10n/TranslationsSection";
import { HeaderTextFields } from "@/l10n/HeaderTextFields";
import { useState, type ReactNode } from "react";
import { ChevronDown, ChevronUp, CircleAlert, Plus, Trash2 } from "lucide-react";
import { useIndex, useProject } from "@/api/queries";
import type { ElementSummary, StereotypeDoc } from "@/api/types";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { Field, Input, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { KindIcon } from "@/app/icons";
import { useServices } from "@/app/context";
import { useEditor, type Draft } from "@/state/store";
import { displayName, KIND_LABELS } from "@/model/model";
import { EDITOR_LABELS, EDITOR_TAB_LABELS, GROUP_LABELS } from "@/model/labels";
import { local } from "@/lib/storage";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { indexLookup } from "@/model/index";
import { useDefinition } from "@/inspector/definition";
import { useDraftDocument } from "@/inspector/useDraft";
import { markDomainOf } from "@/model/vocabularies";
import { ChipsEditor, setOptional, TextField, useVocabularies, type FormProps } from "@/inspector/fields";
import { applicableExtensions, SchemaForm } from "@/inspector/SchemaForm";
import { statusBadge, useInspectorContext } from "@/inspector/Inspector";
import { showsElement } from "@/inspector/context";
import { setView, type EditorKind } from "./tabs";

type Rec = Record<string, unknown>;

export interface EditorSubTab {
  value: string;
  label: string;
  content: ReactNode;
  /** Shown but not selectable (the entity editor's Inheritance tab outside a hierarchy); the frame falls back to the first tab. */
  disabled?: boolean;
  /** The trigger's tooltip, such as why the tab is disabled. */
  title?: string;
  /** The content fills the tab and scrolls itself (the Seed data grid): no padding, no outer scroll. */
  fill?: boolean;
}

/** The DOM id prefix of an editor's fields: distinct from the inspector's when both show one element. */
export const domIdOf = (id: string) => `editor-${id}`;

export interface EditorContext extends FormProps {
  kind: EditorKind;
  summary: ElementSummary | undefined;
  name: string;
}

/** Loads an element for an editor: its context once loaded, else the loading or error state to render. */
export function useEditorContext(id: string, kind: EditorKind): { ctx: EditorContext | null; fallback: ReactNode; draft: Draft | undefined } {
  const { element, draft, json, edit, flush } = useDraftDocument(id);
  const index = useIndex();
  const summary = indexLookup(index.data).byId.get(id);
  if (element.isPending && !json) return { ctx: null, fallback: <Spinner label="Loading element" />, draft };
  if (element.error && !json)
    return { ctx: null, fallback: <EmptyState title="This element could not be loaded">{String((element.error as Error).message)}</EmptyState>, draft };
  if (!json) return { ctx: null, fallback: null, draft };
  const name = String((json as Rec).name ?? "") || displayName(summary);
  return {
    ctx: { id, kind, json, doc: element.data, edit, flush: () => void flush(), diagnostics: draft?.diagnostics ?? [], summary, name },
    fallback: null,
    draft,
  };
}

/** The editor's layout: header, top controls and sub-tabs. */
export function EditorLayout({
  ctx,
  draft,
  controls,
  tabs,
}: {
  ctx: EditorContext;
  draft: Draft | undefined;
  controls: ReactNode;
  tabs: (EditorSubTab | null)[];
}) {
  const { store } = useServices();
  const { id, kind, name } = ctx;
  const view = useEditor(store, (s) => s.editors.view[kind]);
  const list = tabs.filter((t): t is EditorSubTab => !!t);
  const current = list.some((t) => t.value === view && !t.disabled) ? view! : list[0]?.value;
  const diagnostics = draft?.status === "invalid" ? ctx.diagnostics : [];
  // The display names, the description and the top controls can be folded away (a chart or a grid then gets the room;
  // the inspector shows the same fields); the choice is kept per browser and kind. A process starts folded: its chart
  // is what it opens on, and the fields it hides are all in the inspector.
  const [details, setDetails] = useState(() => (local.get(`mq.editor.details.${kind}`) ?? (kind === "process" ? "hidden" : "shown")) !== "hidden");
  const toggleDetails = () => {
    local.set(`mq.editor.details.${kind}`, details ? "hidden" : "shown");
    setDetails(!details);
  };

  return (
    <section aria-label={`Editor: ${name}`} className="flex h-full min-h-0 flex-col bg-surface" data-testid="element-editor" data-kind={kind} data-id={id}>
      <header className="flex flex-col gap-2 border-b border-default px-2 py-1">
        <div className="flex items-center gap-2">
          <KindIcon kind={kind} />
          <h2 className="min-w-0 truncate text-16 font-semibold" data-testid="editor-title">
            {name}
          </h2>
          <span className="text-12 text-secondary">{KIND_LABELS[kind]}</span>
          <span className="flex-1" />
          <span data-testid="editor-save-status">{statusBadge(draft?.status, false)}</span>
          <Button
            variant="ghost"
            size="icon-row"
            label={details ? EDITOR_LABELS.hideDetails : EDITOR_LABELS.showDetails}
            aria-expanded={details}
            data-testid="editor-details-toggle"
            onClick={toggleDetails}
          >
            {details ? <ChevronUp /> : <ChevronDown />}
          </Button>
        </div>
        {draft?.error ? (
          <p role="alert" className="text-12 text-danger">
            {draft.error}
          </p>
        ) : null}
        {diagnostics.length ? (
          <ul role="alert" className="flex flex-col gap-0.5 text-12 text-danger">
            {diagnostics.map((d, i) => (
              <li key={i} className="flex gap-1">
                <CircleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
                <span>
                  <span className="font-mono">{d.rule}</span> {d.message}
                </span>
              </li>
            ))}
          </ul>
        ) : null}
        {details ? <HeaderFields {...ctx} /> : null}
      </header>
      {details ? (
        <div className="border-b border-default px-2 py-1" data-testid="editor-controls">
          {controls}
        </div>
      ) : null}
      <Tabs value={current} onValueChange={(v) => store.getState().updateEditors((s) => setView(s, kind, v))} className="flex min-h-0 flex-1 flex-col">
        <TabsList aria-label={`${KIND_LABELS[kind]} editor views`}>
          {list.map((t) => (
            <TabsTrigger key={t.value} value={t.value} disabled={t.disabled} title={t.title}>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>
        {list.map((t) => (
          <TabsContent key={t.value} value={t.value} className={t.fill ? "flex min-h-0 flex-1 flex-col" : "min-h-0 flex-1 overflow-auto p-2"}>
            {t.content}
          </TabsContent>
        ))}
      </Tabs>
    </section>
  );
}

/** Display names and the description, then the collapsed Translations section when the project declares two or more
 * locales (RT 3.10). */
function HeaderFields({ id, kind, json, doc, edit, flush }: EditorContext) {
  const { store } = useServices();
  // RT 3.10 places Translations in the inspector: the editor shows the section only while the inspector does not
  // show this element, so its inputs never appear twice.
  const context = useInspectorContext();
  const inInspector = useEditor(store, (st) => !st.inspectorCollapsed) && showsElement(context, id);
  const rec = json as Rec;
  const dom = domIdOf(id);
  const description = rec.description;
  return (
    <div className="flex flex-col gap-2">
      <HeaderTextFields
        id={id}
        dom={dom}
        rec={rec}
        edit={(change) => edit((j) => change(j as Rec))}
        flush={flush}
        sidecar={
          typeof description === "object" && description !== null ? { file: (description as { file: string }).file, text: doc?.sidecarText ?? "" } : null
        }
      />
      {inInspector ? null : <TranslationsSection id={id} kind={kind} />}
    </div>
  );
}

/** Name and domain, the first two top controls of every editor. */
export function NameAndDomain({ id, json, edit, flush, diagnostics, children }: EditorContext & { children?: ReactNode }) {
  const rec = json as Rec;
  const dom = domIdOf(id);
  const vocab = useVocabularies(String(rec.kind));
  const definition = useDefinition();
  return (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] items-end gap-2">
      <TextField
        id={`${dom}-name`}
        label="Name"
        value={String(rec.name ?? "")}
        invalid={diagnostics.some((d) => d.jsonPointer === "/name")}
        onChange={(v) => edit((j) => void ((j as Rec).name = v))}
        onBlur={flush}
      />
      <Field label={KIND_LABELS.package} htmlFor={`${dom}-package`}>
        <Select
          {...definition.props(String(rec.package ?? "") || null)}
          id={`${dom}-package`}
          value={String(rec.package ?? "")}
          onChange={(e) => {
            edit((j) => setOptional(j as Rec, "package", e.target.value));
            flush();
          }}
        >
          <option value="">{GROUP_LABELS.notInDomain}</option>
          {vocab.lookup.ofKind("package").map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>
      </Field>
      {children}
    </div>
  );
}

/** Stereotype, tag and category chips. */
export function MarkChips({ json, edit, flush }: EditorContext) {
  const rec = json as Rec;
  const vocab = useVocabularies(String(rec.kind), markDomainOf(rec));
  const category = rec.category ? [String(rec.category)] : [];
  return (
    <div className="grid grid-cols-[repeat(auto-fill,minmax(14rem,1fr))] gap-2" data-testid="editor-chips">
      <ChipsEditor
        label="Stereotypes"
        values={(rec.stereotypes as string[] | undefined) ?? []}
        options={vocab.stereotypes.map((s) => ({ value: s.key, label: `«${s.key}»` }))}
        allowFree={false}
        onChange={(values) => {
          edit((j) => setOptional(j as Rec, "stereotypes", values.length ? values : undefined));
          flush();
        }}
      />
      <ChipsEditor
        label="Tags"
        values={(rec.tags as string[] | undefined) ?? []}
        options={vocab.tags}
        allowFree={!vocab.strictTags}
        onChange={(values) => {
          edit((j) => setOptional(j as Rec, "tags", values.length ? values : undefined));
          flush();
        }}
      />
      <ChipsEditor
        label="Category"
        values={category}
        options={vocab.categories.map((c) => ({ value: c.value, label: c.label }))}
        allowFree={false}
        onChange={(values) => {
          // One category: a pick replaces the current one.
          edit((j) => setOptional(j as Rec, "category", values.at(-1)));
          flush();
        }}
      />
    </div>
  );
}

type GenerationHints = { skip?: boolean; rename?: string; variables?: Record<string, unknown> };
type Generation = Record<string, GenerationHints>;

/** Sets or clears one pack's hints; an empty `generation` is removed (common.json $defs/generation). */
export function withGenerationHints(generation: Generation | undefined, pack: string, hints: GenerationHints | undefined): Generation | undefined {
  const next: Generation = { ...(generation ?? {}) };
  if (hints === undefined) delete next[pack];
  else next[pack] = hints;
  return Object.keys(next).length ? next : undefined;
}

/** Per-pack generation hints (common.json $defs/generationHints: skip, rename, variables); the key is a pack name or "*". */
function GenerationHintsEditor({ id, json, edit, flush }: EditorContext) {
  const generation = ((json as { generation?: Generation }).generation ?? {}) as Generation;
  const [pack, setPack] = useState("");
  const dom = `${domIdOf(id)}-gen`;
  const set = (name: string, hints: GenerationHints | undefined) =>
    edit((j) => setOptional(j as Rec, "generation", withGenerationHints((j as { generation?: Generation }).generation, name, hints)));
  const packs = Object.keys(generation).sort((a, b) => (a === "*" ? -1 : b === "*" ? 1 : a.localeCompare(b)));
  const adding = pack.trim();
  return (
    <section className="flex flex-col gap-2" data-testid="generation-hints">
      <SectionTitle>Generation hints</SectionTitle>
      <p className="text-12 text-secondary">Hints for one generator pack by name, or for every pack with “*”.</p>
      {packs.map((name) => {
        const hints = generation[name] ?? {};
        const variables = Object.keys(hints.variables ?? {}).length;
        return (
          <div key={name} className="flex flex-col gap-1 rounded-control border border-default p-2" data-testid="generation-pack">
            <div className="flex items-center justify-between gap-2">
              <span className="font-mono text-13">{name === "*" ? "* (every pack)" : name}</span>
              <Button size="icon-sm" variant="ghost" label={`Remove the hints for ${name}`} onClick={() => set(name, undefined)}>
                <Trash2 />
              </Button>
            </div>
            <CheckboxField
              id={`${dom}-${name}-skip`}
              label="Skip (generate nothing for this element)"
              checked={hints.skip === true}
              onChange={(on) => {
                const next = { ...hints };
                if (on) next.skip = true;
                else delete next.skip;
                set(name, next);
              }}
            />
            <Field label="Generated name" htmlFor={`${dom}-${name}-rename`}>
              <Input
                id={`${dom}-${name}-rename`}
                className="font-mono"
                value={hints.rename ?? ""}
                onChange={(e) => {
                  const next = { ...hints };
                  if (e.target.value) next.rename = e.target.value;
                  else delete next.rename;
                  set(name, next);
                }}
                onBlur={flush}
              />
            </Field>
            {variables ? (
              <p className="text-12 text-secondary">
                {variables} template variable{variables === 1 ? "" : "s"} (edit them in the document view).
              </p>
            ) : null}
          </div>
        );
      })}
      <form
        className="flex items-end gap-1"
        onSubmit={(e) => {
          e.preventDefault();
          if (!adding || generation[adding]) return;
          set(adding, {});
          setPack("");
        }}
      >
        <Field label="Pack" htmlFor={`${dom}-new`}>
          <Input id={`${dom}-new`} className="font-mono" placeholder="pack name or *" value={pack} onChange={(e) => setPack(e.target.value)} />
        </Field>
        <Button size="sm" type="submit" disabled={!adding || !!generation[adding]}>
          <Plus /> Add hints
        </Button>
      </form>
    </section>
  );
}

/** The Code generation tab: per-pack generation hints, then the custom properties from the extension schemas that
 * apply (SchemaForm). Every editor kind's document has `generation`, so the tab always shows. */
export function useCodeGenerationTab(ctx: EditorContext): EditorSubTab {
  const project = useProject();
  const vocab = useVocabularies(ctx.kind);
  const stereotypes = ((ctx.json as { stereotypes?: string[] }).stereotypes ?? []) as string[];
  const extensions = applicableExtensions(project.data?.extensions ?? [], ctx.kind, stereotypes);
  const defaults: Record<string, { value: unknown; from: string }> = {};
  for (const key of stereotypes) {
    const s = vocab.allStereotypes.find((x) => x.key === key) as StereotypeDoc | undefined;
    for (const [name, value] of Object.entries((s?.defaultProperties as Record<string, unknown> | undefined) ?? {})) defaults[name] ??= { value, from: key };
  }
  return {
    value: "code-generation",
    label: EDITOR_TAB_LABELS.codeGeneration,
    content: (
      <div className="flex flex-col gap-2">
        <GenerationHintsEditor {...ctx} />
        {extensions.length ? (
          <SchemaForm
            idPrefix={`${domIdOf(ctx.id)}-prop`}
            extensions={extensions}
            json={ctx.json}
            defaults={defaults}
            onChange={(name, value) =>
              ctx.edit((j) => {
                const record = j as { properties?: Record<string, unknown> };
                const next = { ...(record.properties ?? {}) };
                if (value === undefined) delete next[name];
                else next[name] = value;
                if (Object.keys(next).length) record.properties = next;
                else delete record.properties;
              })
            }
          />
        ) : null}
      </div>
    ),
  };
}
