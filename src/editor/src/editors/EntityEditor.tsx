// The entity editor (explorer-redesign.md 3.6): name, domain, key, base entity, Is abstract and the mark chips on
// top; the tabs Attributes, Relationships, Indexes, Mappings, Seed data and References (and Code generation when an
// extension schema applies). Every edit goes through the element's draft (state/drafts.ts).
import { useState } from "react";
import { KeyRound, Plus, Trash2 } from "lucide-react";
import { useElements, useIndex } from "@/api/queries";
import type { AttributeDoc, components, ElementSummary, EntityDoc, RelationDoc, RelationEndDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { KindIcon } from "@/app/icons";
import { useEditorNavigation } from "@/app/navigation";
import { indexLookup } from "@/model/index";
import { displayName, TYPE_KINDS, typeLabel } from "@/model/model";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { newId } from "@/lib/ids";
import { AttributeGrid } from "@/inspector/AttributeGrid";
import { useDefinition } from "@/inspector/definition";
import { References } from "@/inspector/Inspector";
import { setOptional, useVocabularies } from "@/inspector/fields";
import { domIdOf, EditorLayout, MarkChips, NameAndDomain, useCodeGenerationTab, useEditorContext, type EditorContext } from "./EditorFrame";
import { baseChain, relatedOf } from "./related";

type Rec = Record<string, unknown>;
type TableDoc = components["schemas"]["table"];
type KeyStrategy = NonNullable<NonNullable<EntityDoc["key"]>["strategy"]>;
type AlternateKey = NonNullable<EntityDoc["alternateKeys"]>[number];

/** The key strategies of the contract (entity.json key.strategy); the check below fails the build when they drift. */
export const KEY_STRATEGIES = ["application", "database-identity", "sequence", "uuid-v7", "ulid"] as const satisfies readonly KeyStrategy[];
const everyStrategyListed: Exclude<KeyStrategy, (typeof KEY_STRATEGIES)[number]> extends never ? true : false = true;
void everyStrategyListed;

/** The lowest free AlternateKeyN name. */
export function nextAlternateKeyName(keys: readonly AlternateKey[]): string {
  const taken = new Set(keys.map((k) => k.name));
  let n = 1;
  while (taken.has(`AlternateKey${n}`)) n++;
  return `AlternateKey${n}`;
}

/** Ticks or unticks a field of an alternate key; a key left with no field is dropped (entity.json: minItems 1). */
export function toggleAlternateKeyField(keys: readonly AlternateKey[], keyId: string, attribute: string, on: boolean): AlternateKey[] {
  const out: AlternateKey[] = [];
  for (const k of keys) {
    if (k.id !== keyId) {
      out.push(k);
      continue;
    }
    const attributes = on ? [...k.attributes.filter((x) => x !== attribute), attribute] : k.attributes.filter((x) => x !== attribute);
    if (attributes.length) out.push({ ...k, attributes });
  }
  return out;
}

export function EntityEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "entity");
  if (!ctx) return <>{fallback}</>;
  return <EntityBody ctx={ctx} draft={draft} />;
}

function EntityBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex flex-col gap-3">
          <NameAndDomain {...ctx}>
            <KeyControl {...ctx} />
            <BaseControl {...ctx} />
            <CheckboxField
              id={`${domIdOf(ctx.id)}-abstract`}
              label="Is abstract"
              checked={(ctx.json as EntityDoc).abstract === true}
              onChange={(v) => {
                ctx.edit((j) => setOptional(j as Rec, "abstract", v ? true : undefined));
                ctx.flush();
              }}
            />
          </NameAndDomain>
          <MarkChips {...ctx} />
        </div>
      }
      tabs={[
        { value: "attributes", label: EDITOR_TAB_LABELS.attributes, content: <FieldsTab {...ctx} /> },
        { value: "relationships", label: EDITOR_TAB_LABELS.relationships, content: <RelationshipsTab id={ctx.id} /> },
        { value: "indexes", label: EDITOR_TAB_LABELS.indexes, content: <IndexesTab {...ctx} /> },
        { value: "mappings", label: EDITOR_TAB_LABELS.mappings, content: <MappingTab id={ctx.id} /> },
        { value: "seed-data", label: EDITOR_TAB_LABELS.seedData, content: <SeedDataTab id={ctx.id} /> },
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
        codeGeneration,
      ]}
    />
  );
}

// ------------------------------------------------------------------ top controls

function KeyControl({ id, json, edit, flush }: EditorContext) {
  const entity = json as EntityDoc;
  const [open, setOpen] = useState(false);
  const attributes = entity.attributes ?? [];
  const nameOf = (a: string) => attributes.find((x) => x.id === a)?.name ?? a;
  const key = entity.key?.attributes ?? [];
  const alternates = entity.alternateKeys ?? [];
  const toggle = (list: string[], attribute: string, on: boolean) =>
    on ? [...list.filter((x) => x !== attribute), attribute] : list.filter((x) => x !== attribute);
  return (
    <Field label="Key" htmlFor={`${domIdOf(id)}-key`}>
      <div className="flex items-center gap-1">
        <Input
          id={`${domIdOf(id)}-key`}
          readOnly
          className="font-mono"
          value={key.length ? key.map(nameOf).join(" + ") : "(none)"}
          title={alternates.length ? `${alternates.length} alternate key${alternates.length === 1 ? "" : "s"}` : undefined}
        />
        <Button size="sm" onClick={() => setOpen(true)} data-testid="edit-key">
          Edit…
        </Button>
      </div>
      <Dialog
        open={open}
        onOpenChange={(next) => {
          setOpen(next);
          if (!next) flush();
        }}
      >
        <DialogContent
          title={`Keys of ${entity.name}`}
          description="The primary key identifies a row; an alternate key is another unique combination of fields."
        >
          <div className="flex max-h-[60vh] flex-col gap-4 overflow-auto" data-testid="key-dialog">
            <section className="flex flex-col gap-1">
              <SectionTitle>Primary key</SectionTitle>
              {attributes.map((a) => (
                <CheckboxField
                  key={a.id}
                  id={`${domIdOf(id)}-pk-${a.id}`}
                  label={a.name}
                  checked={key.includes(a.id)}
                  onChange={(on) =>
                    edit((j) => {
                      const en = j as EntityDoc;
                      const next = toggle(en.key?.attributes ?? [], a.id, on);
                      if (next.length) en.key = { ...(en.key ?? {}), attributes: next };
                      else delete en.key;
                    })
                  }
                />
              ))}
              {entity.key ? (
                <Field label="Key strategy" htmlFor={`${domIdOf(id)}-strategy`}>
                  <Select
                    id={`${domIdOf(id)}-strategy`}
                    value={entity.key.strategy ?? "application"}
                    onChange={(e) =>
                      edit((j) => {
                        const en = j as EntityDoc;
                        if (en.key) en.key.strategy = e.target.value as NonNullable<EntityDoc["key"]>["strategy"];
                      })
                    }
                  >
                    {KEY_STRATEGIES.map((s) => (
                      <option key={s} value={s}>
                        {s}
                      </option>
                    ))}
                  </Select>
                </Field>
              ) : null}
            </section>
            <section className="flex flex-col gap-2">
              <SectionTitle
                actions={
                  <Button
                    size="sm"
                    variant="ghost"
                    disabled={!attributes.length}
                    onClick={() =>
                      edit((j) => {
                        const en = j as EntityDoc;
                        // A key needs one field (minItems 1): the first field not in the primary key, else the first.
                        const first = attributes.find((a) => !key.includes(a.id)) ?? attributes[0];
                        if (!first) return;
                        const keys = en.alternateKeys ?? [];
                        en.alternateKeys = [...keys, { id: newId(), name: nextAlternateKeyName(keys), attributes: [first.id] }];
                      })
                    }
                  >
                    <Plus /> Add alternate key
                  </Button>
                }
              >
                Alternate keys
              </SectionTitle>
              {alternates.map((k, i) => (
                <div key={k.id} className="flex flex-col gap-1 rounded-control border border-default p-2" data-testid="alternate-key">
                  <div className="flex items-center gap-1">
                    <Input
                      aria-label={`Name of alternate key ${i + 1}`}
                      value={k.name}
                      className="font-mono"
                      onChange={(e) => edit((j) => void ((j as EntityDoc).alternateKeys![i].name = e.target.value))}
                    />
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      aria-label={`Remove alternate key ${k.name}`}
                      onClick={() =>
                        edit((j) => {
                          const en = j as EntityDoc;
                          const next = (en.alternateKeys ?? []).filter((x) => x.id !== k.id);
                          setOptional(en as Rec, "alternateKeys", next.length ? next : undefined);
                        })
                      }
                    >
                      <Trash2 />
                    </Button>
                  </div>
                  <div className="flex flex-wrap gap-x-3">
                    {attributes.map((a) => (
                      <CheckboxField
                        key={a.id}
                        id={`${domIdOf(id)}-ak-${k.id}-${a.id}`}
                        label={a.name}
                        checked={k.attributes.includes(a.id)}
                        onChange={(on) =>
                          edit((j) => {
                            const en = j as EntityDoc;
                            const next = toggleAlternateKeyField(en.alternateKeys ?? [], k.id, a.id, on);
                            setOptional(en as Rec, "alternateKeys", next.length ? next : undefined);
                          })
                        }
                      />
                    ))}
                  </div>
                </div>
              ))}
              {!alternates.length ? <p className="text-12 text-secondary">No alternate keys.</p> : null}
            </section>
          </div>
        </DialogContent>
      </Dialog>
    </Field>
  );
}

function BaseControl({ id, json, edit, flush }: EditorContext) {
  const entity = json as EntityDoc;
  const vocab = useVocabularies("entity");
  const definition = useDefinition();
  return (
    <Field label="Base entity" htmlFor={`${domIdOf(id)}-base`}>
      <Select
        {...definition.props(entity.base)}
        id={`${domIdOf(id)}-base`}
        value={entity.base ?? ""}
        onChange={(e) => {
          edit((j) => setOptional(j as Rec, "base", e.target.value));
          flush();
        }}
      >
        <option value="">(none)</option>
        {vocab.lookup
          .ofKind("entity")
          .filter((e) => e.id !== id)
          .map((e) => (
            <option key={e.id} value={e.id}>
              {e.name}
            </option>
          ))}
      </Select>
    </Field>
  );
}

// ------------------------------------------------------------------ tabs

function FieldsTab({ json, edit, flush, diagnostics }: EditorContext) {
  const entity = json as EntityDoc;
  const vocab = useVocabularies("entity");
  const typeOptions = TYPE_KINDS.flatMap((k) => vocab.lookup.ofKind(k));
  const definition = useDefinition();
  const chain = baseChain(vocab.lookup.byId, entity.base);
  const bases = useElements(chain);
  const inherited = chain.flatMap((b) => ((bases.byId.get(b)?.json as EntityDoc | undefined)?.attributes ?? []).map((a) => ({ from: b, attribute: a })));
  return (
    <div className="flex flex-col gap-4">
      <AttributeGrid
        label={`Attributes of ${entity.name}`}
        attributes={entity.attributes ?? []}
        keyIds={entity.key?.attributes ?? []}
        typeOptions={typeOptions}
        definition={definition}
        diagnostics={diagnostics}
        onChange={(update, commit) => {
          edit((j) => update(j));
          if (commit) flush();
        }}
      />
      {entity.alternateKeys?.length ? (
        <section className="flex flex-col gap-1" data-testid="editor-alternate-keys">
          <SectionTitle>Alternate keys</SectionTitle>
          <ul className="text-13">
            {entity.alternateKeys.map((k) => (
              <li key={k.id} className="flex items-center gap-2">
                <KeyRound className="size-3.5 text-secondary" aria-hidden />
                <span className="font-mono">{k.name}</span>
                <span className="text-secondary">{k.attributes.map((a) => entity.attributes?.find((x) => x.id === a)?.name ?? a).join(", ")}</span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      {chain.length ? (
        <section className="flex flex-col gap-1" data-testid="editor-inherited">
          <SectionTitle>Inherited</SectionTitle>
          {inherited.length ? (
            <table className="w-full text-13" aria-label={`Fields ${entity.name} inherits`}>
              <thead className="text-left text-11 text-secondary">
                <tr>
                  <th className="font-medium">Name</th>
                  <th className="font-medium">Type</th>
                  <th className="font-medium">From</th>
                </tr>
              </thead>
              <tbody>
                {inherited.map(({ from, attribute }) => (
                  <tr key={`${from}:${attribute.id}`} className="text-secondary">
                    <td className="font-mono">{attribute.name}</td>
                    <td>{typeLabel(attribute, vocab.lookup.nameOf)}</td>
                    <td>{vocab.lookup.nameOf(from) ?? from}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <p className="text-12 text-secondary">{bases.byId.size < chain.length ? "Loading the base entities…" : "The base entities have no fields."}</p>
          )}
        </section>
      ) : null}
    </div>
  );
}

export function ElementLink({ summary, secondary, pin = true }: { summary: ElementSummary; secondary?: string; pin?: boolean }) {
  const { openEditor, reveal } = useEditorNavigation();
  return (
    <button
      type="button"
      className="flex w-full items-center gap-2 rounded-control px-2 py-1 text-left text-13 hover:bg-accent-subtle"
      onClick={() => (pin ? openEditor(summary, true) : reveal(summary))}
    >
      <KindIcon kind={summary.kind} />
      <span className="min-w-0 flex-1 truncate">{displayName(summary)}</span>
      {secondary ? <span className="truncate text-12 text-secondary">{secondary}</span> : null}
    </button>
  );
}

/** An end's cardinality as min..max, with the contract's defaults (relation.json: min 0, max *). */
export function cardinalityOf(end: Pick<RelationEndDoc, "min" | "max">): string {
  return `${end.min ?? 0}..${end.max ?? "*"}`;
}

function RelationshipsTab({ id }: { id: string }) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const { relations } = relatedOf(index.data, id);
  // The relation documents (one batched read) carry each end's cardinality and the relation's attributes.
  const docs = useElements(relations.map((r) => r.id));
  if (!relations.length) return <EmptyState title="No relationships">No relationship has this entity at an end.</EmptyState>;
  return (
    <ul className="flex flex-col gap-0.5" aria-label="Relationships of this entity" data-testid="editor-relationships">
      {relations.map((r) => {
        const doc = docs.byId.get(r.id)?.json as RelationDoc | undefined;
        const ends = doc?.ends ?? r.ends ?? [];
        const endText = ends
          .map((e) => {
            const cardinality = doc ? ` ${cardinalityOf(e as RelationEndDoc)}` : "";
            return `${lookup.nameOf(e.entity) ?? e.entity}${e.role ? ` (${e.role})` : ""}${cardinality}`;
          })
          .join(" – ");
        const count = doc?.attributes?.length ?? 0;
        const attributes = doc ? ` · ${count} attribute${count === 1 ? "" : "s"}` : "";
        return (
          <li key={r.id}>
            <ElementLink summary={r} secondary={endText + attributes} />
          </li>
        );
      })}
    </ul>
  );
}

function IndexesTab({ id, json }: EditorContext) {
  const index = useIndex();
  const { tables } = relatedOf(index.data, id);
  const docs = useElements(tables.map((t) => t.id));
  const attributes: AttributeDoc[] = (json as EntityDoc).attributes ?? [];
  const declared = attributes.filter((a) => a.unique || a.indexed);
  const physical = tables.flatMap((t) => {
    const table = docs.byId.get(t.id)?.json as TableDoc | undefined;
    return [
      ...(table?.uniques ?? []).map((u) => ({ table: t, id: u.id, name: u.name ?? "(unnamed)", columns: u.columns.join(", "), unique: true })),
      ...(table?.indexes ?? []).map((x) => ({
        table: t,
        id: x.id,
        name: x.name ?? "(unnamed)",
        columns: x.columns.map((c) => `${c.column}${c.descending ? " desc" : ""}`).join(", "),
        unique: x.unique === true,
      })),
    ];
  });
  if (!declared.length && !physical.length)
    return <EmptyState title="No indexes">Mark a field Unique or Indexed on the Attributes tab, or add an index to a table this entity maps to.</EmptyState>;
  return (
    <div className="flex flex-col gap-4" data-testid="editor-indexes">
      {declared.length ? (
        <section className="flex flex-col gap-1">
          <SectionTitle>On fields</SectionTitle>
          <ul className="text-13">
            {declared.map((a) => (
              <li key={a.id} className="flex gap-2">
                <span className="font-mono">{a.name}</span>
                <span className="text-secondary">{a.unique ? "unique" : "indexed"}</span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      {physical.length ? (
        <section className="flex flex-col gap-1">
          <SectionTitle>On tables</SectionTitle>
          <ul className="text-13">
            {physical.map((x) => (
              <li key={`${x.table.id}:${x.id}`} className="flex gap-2">
                <span className="font-mono">{x.name}</span>
                <span className="text-secondary">
                  {x.table.name} ({x.columns}){x.unique ? ", unique" : ""}
                </span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}

function MappingTab({ id }: { id: string }) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const { openWorkspace, select } = useEditorNavigation();
  const { mappings, tables } = relatedOf(index.data, id);
  const databaseOf = (r: ElementSummary) => (r.database ? (lookup.nameOf(r.database) ?? r.database) : undefined);
  return (
    <div className="flex flex-col gap-4" data-testid="editor-mapping">
      <p className="text-12 text-secondary">
        Columns follow the project's conventions unless a mapping overrides them.{" "}
        <button
          type="button"
          className="text-accent underline-offset-2 hover:underline"
          onClick={() => {
            select([id]);
            openWorkspace("mappings");
          }}
        >
          Open the Mappings screen
        </button>
      </p>
      <section className="flex flex-col gap-1">
        <SectionTitle>Customised mappings</SectionTitle>
        {mappings.length ? (
          <ul>
            {mappings.map((m) => (
              <li key={m.id}>
                <ElementLink summary={m} secondary={databaseOf(m)} pin={false} />
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-12 text-secondary">None: every database maps this entity by convention.</p>
        )}
      </section>
      {tables.length ? (
        <section className="flex flex-col gap-1">
          <SectionTitle>Tables</SectionTitle>
          <ul>
            {tables.map((t) => (
              <li key={t.id}>
                <ElementLink summary={t} secondary={databaseOf(t)} pin={false} />
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}

function SeedDataTab({ id }: { id: string }) {
  const index = useIndex();
  const { seeds } = relatedOf(index.data, id);
  if (!seeds.length) return <EmptyState title="No seed data">No seed data targets this entity.</EmptyState>;
  return (
    <ul className="flex flex-col gap-0.5" data-testid="editor-seeds">
      {seeds.map((s) => (
        <li key={s.id}>
          <ElementLink summary={s} secondary={s.rowCount != null ? `${s.rowCount} row${s.rowCount === 1 ? "" : "s"}` : undefined} pin={false} />
        </li>
      ))}
    </ul>
  );
}
