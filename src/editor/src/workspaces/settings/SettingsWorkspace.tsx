// The Settings workspace (phase2-design.md 4.8; owner decision 7: phase 2 edits conventions and
// vocabularies only). Tags (the tag vocabulary element), categories (tree editor over the
// category tree), stereotypes (list and form), and conventions for the project and per database,
// saved through PUT /api/project/settings with the inherited value as placeholder. Type maps,
// output allowlist and formatters are read-only.
import { useEffect, useMemo, useState } from "react";
import { useLocation } from "react-router";
import { ArrowDown, ArrowUp, Plus, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { applySaveResult, keys, useElements, useIndex, useProject, useSettings } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { CategoryDoc, CategoryTreeDoc, ConventionsJson, ModelJson, SettingsJson, StereotypeDoc, TagVocabularyDoc } from "@/api/types";
import { indexLookup } from "@/model/index";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { useServices } from "@/app/context";
import { useEditorNavigation, parseLocation } from "@/app/navigation";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Button } from "@/components/ui/button";
import { Field, Input, Select } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { EmptyState, SectionTitle, Spinner } from "@/components/ui/misc";
import { useDraftDocument } from "@/inspector/useDraft";
import { setOptional } from "@/inspector/fields";
import { AttributeGrid } from "@/inspector/AttributeGrid";
import { keptConventionsDraft, rebaseConventions, type ConventionsDraft } from "./conventions";
import { TYPE_KINDS, KIND_LABELS } from "@/model/model";

const TABS = ["tags", "categories", "stereotypes", "conventions", "project"] as const;

export function SettingsWorkspace() {
  const location = useLocation();
  const { openSettings } = useEditorNavigation();
  const tab = parseLocation(location.pathname, location.search).settingsTab ?? "tags";
  return (
    <Tabs value={tab} onValueChange={(v) => openSettings(v)} className="flex h-full min-h-0 flex-col bg-app" data-testid="settings-workspace">
      <TabsList aria-label="Settings" className="bg-surface">
        <TabsTrigger value="tags">Tags</TabsTrigger>
        <TabsTrigger value="categories">Categories</TabsTrigger>
        <TabsTrigger value="stereotypes">Stereotypes</TabsTrigger>
        <TabsTrigger value="conventions">Conventions</TabsTrigger>
        <TabsTrigger value="project">Type maps, outputs, formatters</TabsTrigger>
      </TabsList>
      {TABS.map((t) => (
        <TabsContent key={t} value={t} className="overflow-auto p-4">
          {t === "tags" ? (
            <TagsSettings />
          ) : t === "categories" ? (
            <CategoriesSettings />
          ) : t === "stereotypes" ? (
            <StereotypesSettings />
          ) : t === "conventions" ? (
            <ConventionsSettings />
          ) : (
            <ReadOnlySettings />
          )}
        </TabsContent>
      ))}
    </Tabs>
  );
}

function useSingleton(kind: "tag-vocabulary" | "category-tree") {
  const index = useIndex();
  const id = indexLookup(index.data).ofKind(kind)[0]?.id ?? null;
  return { id, loading: index.isPending, ...useDraftDocument(id) };
}

function CreateSingleton({ kind, name }: { kind: "tag-vocabulary" | "category-tree"; name: string }) {
  const { queryClient, store } = useServices();
  return (
    <EmptyState title={`No ${KIND_LABELS[kind].toLowerCase()} yet`}>
      <Button
        className="mt-2"
        variant="primary"
        onClick={async () => {
          const json = { kind, id: newId(), name, ...(kind === "tag-vocabulary" ? { definitions: [] } : { categories: [] }) } as unknown as ModelJson;
          const result = await endpoints.createElement(json);
          if (result.outcome === "saved") applySaveResult(queryClient, result);
          else store.getState().notify(result.diagnostics[0]?.message ?? result.outcome, "error");
        }}
      >
        Create it
      </Button>
    </EmptyState>
  );
}

function TagsSettings() {
  const { id, json, edit, flush, loading } = useSingleton("tag-vocabulary");
  if (loading) return <Spinner />;
  if (!id) return <CreateSingleton kind="tag-vocabulary" name="tags" />;
  if (!json) return <Spinner />;
  const vocab = json as unknown as TagVocabularyDoc;
  const definitions = vocab.definitions ?? [];
  return (
    <section className="flex max-w-3xl flex-col gap-3" aria-label="Tag vocabulary">
      <CheckboxField
        id="tags-strict"
        label="Strict: an undeclared tag is an error (MQ2006)"
        checked={vocab.strict === true}
        onChange={(v) => {
          edit((j) => setOptional(j as never, "strict", v ? true : undefined));
          void flush();
        }}
      />
      <SectionTitle
        actions={
          <Button
            size="sm"
            onClick={() => edit((j) => void ((j as unknown as TagVocabularyDoc).definitions = [...definitions, { key: `tag${definitions.length + 1}` }]))}
          >
            <Plus /> Add tag
          </Button>
        }
      >
        Tags
      </SectionTitle>
      <table className="w-full text-13" aria-label="Tags">
        <thead>
          <tr className="text-left text-11 text-secondary">
            <th className="font-semibold">Key</th>
            <th className="font-semibold">Description</th>
            <th className="font-semibold">Color</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {definitions.map((d, i) => (
            <tr key={i}>
              <td className="py-1 pr-2">
                <Input
                  aria-label={`Key of tag ${i + 1}`}
                  className="font-mono"
                  value={d.key}
                  onChange={(e) => edit((j) => void ((j as unknown as TagVocabularyDoc).definitions![i].key = e.target.value))}
                  onBlur={() => void flush()}
                />
              </td>
              <td className="py-1 pr-2">
                <Input
                  aria-label={`Description of ${d.key}`}
                  value={d.description ?? ""}
                  onChange={(e) => edit((j) => setOptional((j as unknown as TagVocabularyDoc).definitions![i] as never, "description", e.target.value))}
                  onBlur={() => void flush()}
                />
              </td>
              <td className="py-1 pr-2">
                <div className="flex items-center gap-2">
                  <span aria-hidden className="size-4 shrink-0 rounded-[4px] border border-default" style={{ background: d.color }} />
                  <Input
                    aria-label={`Color of ${d.key}`}
                    className="font-mono"
                    value={d.color ?? ""}
                    onChange={(e) => edit((j) => setOptional((j as unknown as TagVocabularyDoc).definitions![i] as never, "color", e.target.value))}
                    onBlur={() => void flush()}
                  />
                </div>
              </td>
              <td>
                <Button
                  size="icon-sm"
                  variant="ghost"
                  aria-label={`Remove tag ${d.key}`}
                  onClick={() => {
                    edit((j) => void ((j as unknown as TagVocabularyDoc).definitions = definitions.filter((_, k) => k !== i)));
                    void flush();
                  }}
                >
                  <Trash2 />
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function CategoriesSettings() {
  const { id, json, edit, flush, loading } = useSingleton("category-tree");
  if (loading) return <Spinner />;
  if (!id) return <CreateSingleton kind="category-tree" name="categories" />;
  if (!json) return <Spinner />;
  const tree = json as unknown as CategoryTreeDoc;
  const categories = tree.categories ?? [];
  const children = (parent: string | null) => categories.filter((c) => (c.parent ?? null) === parent).sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
  const mutate = (fn: (list: CategoryDoc[]) => void) => {
    edit((j) => {
      const t = j as unknown as CategoryTreeDoc;
      const list = t.categories ?? [];
      fn(list);
      t.categories = list;
    });
    void flush();
  };
  const move = (c: CategoryDoc, delta: -1 | 1) =>
    mutate((list) => {
      const siblings = list.filter((x) => (x.parent ?? null) === (c.parent ?? null)).sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
      const at = siblings.findIndex((x) => x.id === c.id);
      const other = siblings[at + delta];
      if (!other) return;
      siblings.forEach((s, i) => (s.order = i + 1));
      const a = list.find((x) => x.id === c.id)!;
      const b = list.find((x) => x.id === other.id)!;
      [a.order, b.order] = [b.order, a.order];
    });
  const render = (parent: string | null, depth: number) => (
    <ul className="flex flex-col gap-1" role={depth === 0 ? "tree" : "group"} aria-label={depth === 0 ? "Categories" : undefined}>
      {children(parent).map((c) => (
        <li key={c.id} role="treeitem" aria-level={depth + 1} aria-selected={false}>
          <div className="flex items-center gap-1" style={{ paddingLeft: depth * 20 }}>
            <Input
              aria-label={`Name of category ${c.name}`}
              className="h-7 w-56"
              value={c.name}
              onChange={(e) =>
                edit((j) => {
                  const cat = (j as unknown as CategoryTreeDoc).categories!.find((x) => x.id === c.id);
                  if (cat) cat.name = e.target.value;
                })
              }
              onBlur={() => void flush()}
            />
            <Button size="icon-sm" variant="ghost" aria-label={`Move ${c.name} up`} onClick={() => move(c, -1)}>
              <ArrowUp />
            </Button>
            <Button size="icon-sm" variant="ghost" aria-label={`Move ${c.name} down`} onClick={() => move(c, 1)}>
              <ArrowDown />
            </Button>
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Add a category under ${c.name}`}
              onClick={() => mutate((list) => void list.push({ id: newId(), name: "New category", parent: c.id, order: children(c.id).length + 1 }))}
            >
              <Plus />
            </Button>
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Remove ${c.name}`}
              disabled={children(c.id).length > 0}
              onClick={() =>
                mutate(
                  (list) =>
                    void list.splice(
                      list.findIndex((x) => x.id === c.id),
                      1,
                    ),
                )
              }
            >
              <Trash2 />
            </Button>
          </div>
          {children(c.id).length ? render(c.id, depth + 1) : null}
        </li>
      ))}
    </ul>
  );
  return (
    <section className="flex max-w-3xl flex-col gap-3" aria-label="Category tree">
      <SectionTitle
        actions={
          <Button size="sm" onClick={() => mutate((list) => void list.push({ id: newId(), name: "New category", order: children(null).length + 1 }))}>
            <Plus /> Add category
          </Button>
        }
      >
        Categories
      </SectionTitle>
      {render(null, 0)}
      <p className="text-12 text-secondary">
        Categories color entity cards (in tree order) and group the explorer's category filter. A category still used by an element cannot be removed without a
        validation error (MQ2005).
      </p>
    </section>
  );
}

function StereotypesSettings() {
  const { queryClient, store } = useServices();
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const ids = lookup.ofKind("stereotype").map((s) => s.id);
  const docs = useElements(ids);
  const [selected, setSelected] = useState<string | null>(null);
  const [newKey, setNewKey] = useState("");
  const current = selected ?? ids[0] ?? null;
  const { json, edit, flush } = useDraftDocument(current);
  const s = json as unknown as StereotypeDoc | undefined;
  const typeOptions = TYPE_KINDS.flatMap((k) => lookup.ofKind(k));
  const kinds = ["entity", "value-object", "enum", "relation", "attribute", "database", "table", "package"];
  return (
    <section className="grid max-w-5xl grid-cols-[220px_1fr] gap-4" aria-label="Stereotypes">
      <div className="flex flex-col gap-2">
        <ul className="flex flex-col gap-0.5" aria-label="Stereotype list">
          {ids.map((id) => {
            const doc = docs.byId.get(id)?.json as unknown as StereotypeDoc | undefined;
            return (
              <li key={id}>
                <button
                  type="button"
                  className={`w-full rounded-control px-2 py-1 text-left text-13 ${id === current ? "bg-accent-subtle" : "hover:bg-surface"}`}
                  onClick={() => setSelected(id)}
                  aria-current={id === current || undefined}
                >
                  «{doc?.key ?? "…"}» <span className="text-secondary">{doc?.name}</span>
                </button>
              </li>
            );
          })}
        </ul>
        <form
          className="flex gap-1"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!/^[a-z][a-z0-9]*(-[a-z0-9]+)*$/.test(newKey)) {
              store.getState().notify("A stereotype key is kebab-case, such as audited or soft-delete.", "error");
              return;
            }
            const json = { kind: "stereotype", id: newId(), key: newKey, name: newKey, appliesTo: ["entity"] } as unknown as ModelJson;
            const result = await endpoints.createElement(json);
            if (result.outcome === "saved") {
              applySaveResult(queryClient, result);
              setSelected(result.id);
              setNewKey("");
            } else store.getState().notify(result.diagnostics[0]?.message ?? result.outcome, "error");
          }}
        >
          <Input aria-label="New stereotype key" placeholder="new-key" value={newKey} onChange={(e) => setNewKey(e.target.value)} className="font-mono" />
          <Button type="submit" size="icon" aria-label="Add stereotype">
            <Plus />
          </Button>
        </form>
      </div>
      {s ? (
        <div className="flex flex-col gap-3 rounded-panel border border-default bg-surface p-3">
          <div className="grid grid-cols-2 gap-2">
            <Field label="Key" htmlFor="st-key" hint="Fixed once created (MQ3020).">
              <Input id="st-key" value={s.key} readOnly className="font-mono" />
            </Field>
            <Field label="Name" htmlFor="st-name">
              <Input
                id="st-name"
                value={s.name}
                onChange={(e) => edit((j) => void ((j as unknown as StereotypeDoc).name = e.target.value))}
                onBlur={() => void flush()}
              />
            </Field>
          </div>
          <Field label="Applies to">
            <div className="flex flex-wrap gap-3">
              {kinds.map((k) => (
                <CheckboxField
                  key={k}
                  id={`st-applies-${k}`}
                  label={k}
                  checked={(s.appliesTo ?? []).includes(k as never)}
                  onChange={(v) => {
                    edit((j) => {
                      const st = j as unknown as StereotypeDoc;
                      const set = new Set(st.appliesTo ?? []);
                      if (v) set.add(k as never);
                      else set.delete(k as never);
                      st.appliesTo = [...set] as StereotypeDoc["appliesTo"];
                    });
                    void flush();
                  }}
                />
              ))}
            </div>
          </Field>
          <Field label="Default properties (JSON)" htmlFor="st-defaults" hint="Values for custom properties of elements with this stereotype.">
            <DefaultsEditor
              value={s.defaultProperties ?? {}}
              onChange={(v) => {
                edit((j) => setOptional(j as never, "defaultProperties", Object.keys(v).length ? v : undefined));
                void flush();
              }}
            />
          </Field>
          <SectionTitle>Attributes it adds</SectionTitle>
          <AttributeGrid
            label={`Attributes of «${s.key}»`}
            attributes={s.attributes ?? []}
            typeOptions={typeOptions}
            withKey={false}
            onChange={(update, commit) => {
              edit((j) => update(j));
              if (commit) void flush();
            }}
          />
        </div>
      ) : (
        <EmptyState title="No stereotypes yet" />
      )}
    </section>
  );
}

function DefaultsEditor({ value, onChange }: { value: Record<string, unknown>; onChange: (v: Record<string, unknown>) => void }) {
  const [text, setText] = useState(JSON.stringify(value));
  const [error, setError] = useState<string | null>(null);
  useEffect(() => setText(JSON.stringify(value)), [value]);
  return (
    <div>
      <Input
        id="st-defaults"
        className="font-mono"
        value={text}
        aria-invalid={!!error || undefined}
        onChange={(e) => setText(e.target.value)}
        onBlur={() => {
          try {
            const parsed = JSON.parse(text) as unknown;
            if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("an object is expected");
            setError(null);
            onChange(parsed as Record<string, unknown>);
          } catch (e) {
            setError((e as Error).message);
          }
        }}
      />
      {error ? (
        <p role="alert" className="text-12 text-danger">
          {error}
        </p>
      ) : null}
    </div>
  );
}

type ConventionKey = keyof ConventionsJson;
const CONVENTION_FIELDS: { key: ConventionKey; kind: "enum" | "bool" | "text" | "int"; options?: string[]; engineDefault?: string }[] = [
  { key: "tableCase", kind: "enum", options: ["snake", "pascal", "camel", "kebab", "upper-snake", "preserve"], engineDefault: "snake" },
  { key: "columnCase", kind: "enum", options: ["snake", "pascal", "camel", "kebab", "upper-snake", "preserve"], engineDefault: "snake" },
  { key: "pluralTables", kind: "bool", engineDefault: "true" },
  { key: "tableName", kind: "text" },
  { key: "keyColumn", kind: "text" },
  { key: "foreignKeyColumn", kind: "text" },
  { key: "junctionTable", kind: "text" },
  { key: "childTable", kind: "text" },
  { key: "valueObjectColumn", kind: "text" },
  { key: "orderColumn", kind: "text" },
  { key: "discriminatorColumn", kind: "text" },
  { key: "primaryKeyName", kind: "text" },
  { key: "foreignKeyName", kind: "text" },
  { key: "uniqueName", kind: "text" },
  { key: "indexName", kind: "text" },
  { key: "checkName", kind: "text" },
  { key: "sequenceName", kind: "text" },
  { key: "defaultStringLength", kind: "int" },
  { key: "decimalPrecision", kind: "int" },
  { key: "decimalScale", kind: "int" },
  { key: "datetimePrecision", kind: "int" },
  { key: "enumStorage", kind: "enum", options: ["int", "string", "lookup"], engineDefault: "int" },
  { key: "valueObjectStorage", kind: "enum", options: ["embedded", "table", "json"], engineDefault: "embedded" },
  { key: "valueObjectCollectionStorage", kind: "enum", options: ["table", "json"] },
  { key: "relationsWithAttributes", kind: "enum", options: ["junction", "promoted"] },
  { key: "inheritance", kind: "enum", options: ["tph", "tpt", "tpc"] },
];

function ConventionsSettings() {
  const settings = useSettings();
  const project = useProject();
  const qc = useQueryClient();
  const { store } = useServices();
  const [scope, setScope] = useState<string>("");
  const [draftState, setDraftState] = useState<ConventionsDraft | null>(() => keptConventionsDraft.get());
  const setDraft = (next: ConventionsDraft | null) => {
    keptConventionsDraft.set(next);
    setDraftState(next);
  };
  const draft = draftState?.json ?? null;
  const [saving, setSaving] = useState(false);
  const [diagnostics, setDiagnostics] = useState<string[]>([]);
  const base = settings.data?.json as SettingsJson | undefined;
  const json = draft ?? base;
  const databases = project.data?.databases ?? [];
  const conventions = useMemo(() => {
    if (!json) return {} as ConventionsJson;
    return (scope ? (json.databases?.[scope] ?? {}) : (json.conventions ?? {})) as ConventionsJson;
  }, [json, scope]);
  const inherited = (key: ConventionKey): string => {
    if (!json) return "";
    const fromProject = scope ? (json.conventions as Record<string, unknown> | undefined)?.[key] : undefined;
    if (fromProject !== undefined) return `${String(fromProject)} (project)`;
    const field = CONVENTION_FIELDS.find((f) => f.key === key);
    return field?.engineDefault ? `${field.engineDefault} (engine default)` : "engine default";
  };
  const set = (key: ConventionKey, value: unknown) => {
    const next = clone(json!) as SettingsJson;
    const target = (scope ? ((next.databases ??= {})[scope] ??= {}) : (next.conventions ??= {})) as Record<string, unknown>;
    if (value === undefined || value === "") delete target[key];
    else target[key] = value;
    if (scope && next.databases && Object.keys(next.databases[scope] ?? {}).length === 0) delete next.databases[scope];
    if (next.databases && Object.keys(next.databases).length === 0) delete next.databases;
    if (!scope && next.conventions && Object.keys(next.conventions).length === 0) delete next.conventions;
    if (draftState) setDraft({ ...draftState, json: next });
    else if (settings.data) setDraft({ baseHash: settings.data.hash, baseJson: clone(base!), json: next });
  };
  const save = async () => {
    if (!draftState) return;
    setSaving(true);
    try {
      // The hash the draft was built on: a change on disk since then must come back as a 409.
      const result = await endpoints.saveSettings(draftState.json, draftState.baseHash);
      if (result.outcome === "saved" && result.current) {
        qc.setQueryData(keys.settings, result.current);
        void qc.invalidateQueries({ queryKey: keys.project });
        setDraft(null);
        setDiagnostics([]);
        store.getState().notify("Conventions saved.");
      } else if (result.outcome === "conflict") {
        // Re-apply only the keys changed here onto the disk version; the user reviews and saves again.
        const disk = await qc.fetchQuery({ queryKey: keys.settings, queryFn: endpoints.getSettings, staleTime: 0 });
        setDraft({
          baseHash: disk.hash,
          baseJson: clone(disk.json as SettingsJson),
          json: rebaseConventions(draftState.baseJson, draftState.json, disk.json as SettingsJson),
        });
        store
          .getState()
          .notify("maquettiste.json changed on disk. Your convention changes were re-applied onto the new version; review them and save again.", "error");
      } else setDiagnostics(result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer ?? ""} ${d.message}`));
    } finally {
      setSaving(false);
    }
  };
  if (settings.isPending || !json) return <Spinner label="Loading settings" />;
  return (
    <section className="flex max-w-4xl flex-col gap-3" aria-label="Conventions">
      <div className="flex items-center gap-2">
        <label htmlFor="conv-scope" className="text-12 text-secondary">
          Scope
        </label>
        <Select id="conv-scope" className="w-56" value={scope} onChange={(e) => setScope(e.target.value)}>
          <option value="">Project (every database)</option>
          {databases.map((d) => (
            <option key={d.id} value={d.name}>
              Database {d.name}
            </option>
          ))}
        </Select>
        <span className="ml-auto flex items-center gap-2">
          {draft ? <span className="text-12 text-secondary">Unsaved changes</span> : null}
          <Button onClick={() => setDraft(null)} disabled={!draft || saving}>
            Discard
          </Button>
          <Button variant="primary" onClick={() => void save()} disabled={!draft || saving} data-testid="save-conventions">
            Save
          </Button>
        </span>
      </div>
      {diagnostics.length ? (
        <ul role="alert" className="text-12 text-danger">
          {diagnostics.map((d, i) => (
            <li key={i}>{d}</li>
          ))}
        </ul>
      ) : null}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-3">
        {CONVENTION_FIELDS.map((f) => {
          const id = `conv-${f.key}`;
          const value = (conventions as Record<string, unknown>)[f.key];
          if (f.kind === "enum" || f.kind === "bool")
            return (
              <Field key={f.key} label={f.key} htmlFor={id}>
                <Select
                  id={id}
                  value={value === undefined ? "" : String(value)}
                  onChange={(e) => set(f.key, e.target.value === "" ? undefined : f.kind === "bool" ? e.target.value === "true" : e.target.value)}
                >
                  <option value="">inherit: {inherited(f.key)}</option>
                  {(f.kind === "bool" ? ["true", "false"] : f.options!).map((o) => (
                    <option key={o} value={o}>
                      {o}
                    </option>
                  ))}
                </Select>
              </Field>
            );
          return (
            <Field key={f.key} label={f.key} htmlFor={id}>
              <Input
                id={id}
                className={f.kind === "text" ? "font-mono" : undefined}
                placeholder={`inherit: ${inherited(f.key)}`}
                value={value === undefined ? "" : String(value)}
                onChange={(e) =>
                  set(
                    f.key,
                    e.target.value === ""
                      ? undefined
                      : f.kind === "int"
                        ? /^\d+$/.test(e.target.value)
                          ? Number(e.target.value)
                          : e.target.value
                        : e.target.value,
                  )
                }
              />
            </Field>
          );
        })}
      </div>
    </section>
  );
}

function ReadOnlySettings() {
  const project = useProject();
  const s = project.data?.settings;
  if (!s) return <Spinner />;
  return (
    <section className="flex max-w-4xl flex-col gap-4" aria-label="Read-only settings">
      <p className="text-12 text-secondary">Phase 2 shows these read-only; edit maquettiste.json on disk to change them.</p>
      <div>
        <SectionTitle>Output allowlist</SectionTitle>
        <ul className="font-mono text-12">
          {s.outputs.allow.map((a) => (
            <li key={a.path}>
              allow {a.path} {a.commit ? "(committed)" : "(built)"}
            </li>
          ))}
          {s.outputs.deny.map((d) => (
            <li key={d}>deny {d}</li>
          ))}
        </ul>
      </div>
      <div>
        <SectionTitle>Type maps</SectionTitle>
        {Object.keys(s.typeMaps).length ? (
          <pre className="rounded-control border border-default bg-surface p-2 text-12">{JSON.stringify(s.typeMaps, null, 2)}</pre>
        ) : (
          <p className="text-12 text-secondary">None: every dialect uses its built-in type map.</p>
        )}
      </div>
      <div>
        <SectionTitle>Formatters</SectionTitle>
        {s.formatters.length ? (
          <ul className="font-mono text-12">
            {s.formatters.map((f) => (
              <li key={f.name}>
                {f.name}: {f.command} {f.args.join(" ")} ({f.extensions.join(", ")}) version {f.version}
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-12 text-secondary">No formatters configured.</p>
        )}
      </div>
      <div>
        <SectionTitle>Packs</SectionTitle>
        <ul className="font-mono text-12">
          {Object.entries(s.packs).map(([name, p]) => (
            <li key={name}>
              {name}: {p.enabled ? "enabled" : "disabled"}, output {p.output || "(root)"}
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
