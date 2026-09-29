// Property forms for the kinds the phase 2 workspaces edit. Every edit goes through the draft;
// text fields save 600 ms after the last keystroke or on blur.
import { useDefinition } from "./definition";
import { useMemo, useState } from "react";
import { Plus, X } from "lucide-react";
import type {
  DatabaseDoc,
  ElementDocument,
  ElementSummary,
  EntityDoc,
  EnumDoc,
  ModelJson,
  RelationDoc,
  RelationEndDoc,
  ScalarTypeDoc,
  StereotypeDoc,
} from "@/api/types";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { Button } from "@/components/ui/button";
import { SectionTitle } from "@/components/ui/misc";
import { BUILTIN_TYPES, TYPE_KINDS } from "@/model/model";
import { newId } from "@/lib/ids";
import { useElements, useIndex } from "@/api/queries";
import { indexLookup } from "@/model/index";
import { categoryOptions, markDomainOf, tagOptions, vocabulariesOnChain } from "@/model/vocabularies";
import { AttributeGrid } from "./AttributeGrid";
import type { Diagnostic } from "@/api/types";
import { GROUP_LABELS, KIND_LABELS } from "@/model/labels";

export interface FormProps {
  id: string;
  json: ModelJson;
  doc: ElementDocument | undefined;
  edit: (update: (json: ModelJson) => ModelJson | void) => void;
  flush: () => void;
  diagnostics: Diagnostic[];
}

type Rec = Record<string, unknown>;

/** Sets or removes an optional member (canonical files omit absent members). */
export function setOptional(json: Rec, key: string, value: unknown): void {
  if (value === undefined || value === "" || value === null) delete json[key];
  else json[key] = value;
}

export function TextField({
  id,
  label,
  value,
  onChange,
  onBlur,
  invalid,
  placeholder,
  mono,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (v: string) => void;
  onBlur: () => void;
  invalid?: boolean;
  placeholder?: string;
  mono?: boolean;
}) {
  return (
    <Field label={label} htmlFor={id}>
      <Input
        id={id}
        value={value}
        placeholder={placeholder}
        aria-invalid={invalid || undefined}
        className={mono ? "font-mono" : undefined}
        onChange={(e) => onChange(e.target.value)}
        onBlur={onBlur}
        onKeyDown={(e) => {
          if (e.key === "Enter") onBlur();
        }}
      />
    </Field>
  );
}

export function ChipsEditor({
  label,
  values,
  options,
  allowFree,
  onChange,
}: {
  label: string;
  values: string[];
  options: { value: string; label: string }[];
  allowFree: boolean;
  onChange: (values: string[]) => void;
}) {
  const [adding, setAdding] = useState("");
  const remaining = options.filter((o) => !values.includes(o.value));
  return (
    <Field label={label}>
      <div className="flex flex-wrap items-center gap-1">
        {values.map((v) => (
          <span key={v} className="inline-flex h-6 items-center gap-1 rounded-control border border-default bg-app px-1.5 text-12">
            {options.find((o) => o.value === v)?.label ?? v}
            <button
              type="button"
              aria-label={`Remove ${v} from ${label}`}
              onClick={() => onChange(values.filter((x) => x !== v))}
              className="rounded-[3px] text-secondary hover:text-primary"
            >
              <X className="size-3" />
            </button>
          </span>
        ))}
        {allowFree ? (
          <Input
            aria-label={`Add to ${label}`}
            className="h-6 w-28 text-12"
            placeholder="add…"
            value={adding}
            list={`${label}-options`}
            onChange={(e) => setAdding(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && adding.trim()) {
                onChange([...values, adding.trim()]);
                setAdding("");
              }
            }}
          />
        ) : remaining.length ? (
          <Select
            aria-label={`Add to ${label}`}
            className="h-6 w-auto text-12"
            value=""
            onChange={(e) => e.target.value && onChange([...values, e.target.value])}
          >
            <option value="">add…</option>
            {remaining.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        ) : null}
        {allowFree ? (
          <datalist id={`${label}-options`}>
            {remaining.map((o) => (
              <option key={o.value} value={o.value} label={o.label} />
            ))}
          </datalist>
        ) : null}
      </div>
    </Field>
  );
}

/**
 * The stereotypes, and the tags and categories an element in `domain` is offered (explorer-redesign.md 1.11): its
 * domain's vocabularies, each enclosing domain's, then the global ones, nearest first; a domain entry names its domain.
 */
export function useVocabularies(kind: string, domain: string | null = null) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const rows = index.data;
  const tagChain = useMemo(() => vocabulariesOnChain("tag-vocabulary", domain, rows ?? []), [rows, domain]);
  const categoryChain = useMemo(() => vocabulariesOnChain("category-tree", domain, rows ?? []), [rows, domain]);
  const vocabularyIds = useMemo(() => [...tagChain, ...categoryChain].map((v) => v.id), [tagChain, categoryChain]);
  const vocabularyDocs = useElements(vocabularyIds);
  const stereotypeIds = lookup.ofKind("stereotype").map((s) => s.id);
  const stereotypeDocs = useElements(stereotypeIds);
  return useMemo(() => {
    const docOf = (id: string) => vocabularyDocs.byId.get(id)?.json;
    const categories = categoryOptions(categoryChain, docOf);
    const tags = tagOptions(tagChain, docOf);
    const stereotypes = stereotypeIds
      .map((id) => stereotypeDocs.byId.get(id)?.json as StereotypeDoc | undefined)
      .filter((s): s is StereotypeDoc => !!s)
      .filter((s) => !s.appliesTo?.length || (s.appliesTo as string[]).includes(kind));
    return {
      lookup,
      categories,
      tags: tags.options,
      strictTags: tags.strict,
      stereotypes,
      allStereotypes: stereotypeIds.map((id) => stereotypeDocs.byId.get(id)?.json as StereotypeDoc | undefined).filter((s): s is StereotypeDoc => !!s),
    };
  }, [vocabularyDocs, tagChain, categoryChain, stereotypeDocs, stereotypeIds, lookup, kind]);
}

/**
 * The fields every element has. `inEditorHeader`: the element editor's header already shows Display name, Plural name
 * and Description (EditorFrame's HeaderFields), so a form inside an editor tab leaves them out.
 */
export function CommonFields({ id, json, doc, edit, flush, diagnostics, inEditorHeader = false }: FormProps & { inEditorHeader?: boolean }) {
  const rec = json as Rec;
  const kind = String(rec.kind);
  const vocab = useVocabularies(kind, markDomainOf(rec));
  const invalid = (pointer: string) => diagnostics.some((d) => d.jsonPointer === pointer);
  const hasPackage = ["entity", "value-object", "scalar-type", "enum", "relation", "diagram"].includes(kind);
  const description = rec.description;
  const definition = useDefinition();
  return (
    <div className="flex flex-col gap-3">
      {kind === "stereotype" ? (
        <Field label="Key" htmlFor={`${id}-key`} hint="A stereotype's key cannot change once it exists (MQ3020).">
          <Input id={`${id}-key`} value={String(rec.key ?? "")} readOnly className="font-mono" />
        </Field>
      ) : null}
      <TextField
        id={`${id}-name`}
        label="Name"
        value={String(rec.name ?? "")}
        invalid={invalid("/name")}
        onChange={(v) => edit((j) => void ((j as Rec).name = v))}
        onBlur={flush}
      />
      {inEditorHeader ? null : (
        <div className="grid grid-cols-2 gap-2">
          <TextField
            id={`${id}-display`}
            label="Display name"
            value={String(rec.displayName ?? "")}
            onChange={(v) => edit((j) => setOptional(j as Rec, "displayName", v))}
            onBlur={flush}
          />
          <TextField
            id={`${id}-plural`}
            label="Plural name"
            value={String(rec.pluralName ?? "")}
            onChange={(v) => edit((j) => setOptional(j as Rec, "pluralName", v))}
            onBlur={flush}
          />
        </div>
      )}
      {hasPackage || kind === "package" ? (
        <Field label={kind === "package" ? `Parent ${KIND_LABELS.package.toLowerCase()}` : KIND_LABELS.package} htmlFor={`${id}-package`}>
          <Select
            {...definition.props(String((kind === "package" ? rec.parent : rec.package) ?? "") || null)}
            id={`${id}-package`}
            value={String((kind === "package" ? rec.parent : rec.package) ?? "")}
            onChange={(e) => {
              edit((j) => setOptional(j as Rec, kind === "package" ? "parent" : "package", e.target.value));
              flush();
            }}
          >
            <option value="">{kind === "package" ? "(top level)" : GROUP_LABELS.notInDomain}</option>
            {vocab.lookup
              .ofKind("package")
              .filter((p) => p.id !== id)
              .map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
          </Select>
        </Field>
      ) : null}
      {inEditorHeader ? null : typeof description === "object" && description !== null ? (
        <Field label="Description" hint={`Kept in ${(description as { file: string }).file} next to the model file; edit that file on disk.`}>
          <pre className="max-h-40 overflow-auto whitespace-pre-wrap rounded-control border border-default bg-app p-2 text-12">{doc?.sidecarText ?? ""}</pre>
        </Field>
      ) : (
        <Field label="Description" htmlFor={`${id}-description`}>
          <Textarea
            id={`${id}-description`}
            value={String(description ?? "")}
            onChange={(e) => edit((j) => setOptional(j as Rec, "description", e.target.value))}
            onBlur={flush}
            rows={3}
          />
        </Field>
      )}
      {!["tag-vocabulary", "category-tree"].includes(kind) ? (
        <>
          <Field label="Category" htmlFor={`${id}-category`}>
            <Select
              id={`${id}-category`}
              value={String(rec.category ?? "")}
              aria-invalid={invalid("/category") || undefined}
              onChange={(e) => {
                edit((j) => setOptional(j as Rec, "category", e.target.value));
                flush();
              }}
            >
              <option value="">(none)</option>
              {vocab.categories.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.parent ? "— " : ""}
                  {c.label}
                </option>
              ))}
            </Select>
          </Field>
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
        </>
      ) : null}
    </div>
  );
}

export function EntityFields({ id, json, edit, flush, diagnostics }: FormProps) {
  const entity = json as EntityDoc;
  const vocab = useVocabularies("entity");
  const typeOptions = TYPE_KINDS.flatMap((k) => vocab.lookup.ofKind(k));
  const definition = useDefinition();
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-2 gap-2">
        <Field label="Base entity" htmlFor={`${id}-base`}>
          <Select
            {...definition.props(entity.base)}
            id={`${id}-base`}
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
        <Field label="Key strategy" htmlFor={`${id}-strategy`}>
          <Select
            id={`${id}-strategy`}
            value={entity.key?.strategy ?? "application"}
            disabled={!entity.key}
            onChange={(e) => {
              edit((j) => {
                const en = j as EntityDoc;
                if (en.key) en.key.strategy = e.target.value as NonNullable<EntityDoc["key"]>["strategy"];
              });
              flush();
            }}
          >
            {["application", "database-identity", "sequence", "uuid-v7", "ulid"].map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      <CheckboxField
        id={`${id}-abstract`}
        label="Abstract"
        checked={entity.abstract === true}
        onChange={(v) => {
          edit((j) => setOptional(j as Rec, "abstract", v ? true : undefined));
          flush();
        }}
      />
      <SectionTitle>Attributes</SectionTitle>
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
    </div>
  );
}

export function AttributesOnlyFields({ json, edit, flush, diagnostics }: FormProps) {
  const record = json as { attributes?: EntityDoc["attributes"]; name: string };
  const vocab = useVocabularies(String((json as Rec).kind));
  const typeOptions = TYPE_KINDS.flatMap((k) => vocab.lookup.ofKind(k));
  const definition = useDefinition();
  return (
    <div className="flex flex-col gap-2">
      <SectionTitle>Attributes</SectionTitle>
      <AttributeGrid
        label={`Attributes of ${record.name}`}
        attributes={record.attributes ?? []}
        typeOptions={typeOptions}
        definition={definition}
        diagnostics={diagnostics}
        withKey={false}
        onChange={(update, commit) => {
          edit((j) => update(j));
          if (commit) flush();
        }}
      />
    </div>
  );
}

function EndEditor({
  index,
  end,
  entities,
  onChange,
  flush,
}: {
  index: number;
  end: RelationEndDoc;
  entities: ElementSummary[];
  onChange: (update: (end: RelationEndDoc) => void) => void;
  flush: () => void;
}) {
  const base = `end-${index}`;
  const definition = useDefinition();
  return (
    <fieldset className="flex flex-col gap-2 rounded-control border border-default p-2">
      <legend className="px-1 text-12 font-medium text-secondary">End {index + 1}</legend>
      <Field label="Entity" htmlFor={`${base}-entity`}>
        <Select
          {...definition.props(end.entity)}
          id={`${base}-entity`}
          value={end.entity}
          onChange={(e) => {
            onChange((x) => void (x.entity = e.target.value));
            flush();
          }}
        >
          {entities.map((en) => (
            <option key={en.id} value={en.id}>
              {en.name}
            </option>
          ))}
        </Select>
      </Field>
      <div className="grid grid-cols-2 gap-2">
        <TextField id={`${base}-role`} label="Role" value={end.role} onChange={(v) => onChange((x) => void (x.role = v))} onBlur={flush} mono />
        <TextField
          id={`${base}-navigation`}
          label="Navigation"
          value={end.navigation ?? ""}
          onChange={(v) => onChange((x) => setOptional(x as unknown as Rec, "navigation", v))}
          onBlur={flush}
          mono
        />
      </div>
      <div className="grid grid-cols-3 gap-2">
        <Field label="Min" htmlFor={`${base}-min`}>
          <Select
            id={`${base}-min`}
            value={String(end.min ?? 0)}
            onChange={(e) => {
              onChange((x) => setOptional(x as unknown as Rec, "min", e.target.value === "1" ? 1 : undefined));
              flush();
            }}
          >
            <option value="0">0</option>
            <option value="1">1</option>
          </Select>
        </Field>
        <Field label="Max" htmlFor={`${base}-max`}>
          <Select
            id={`${base}-max`}
            value={String(end.max ?? "*")}
            onChange={(e) => {
              onChange((x) => setOptional(x as unknown as Rec, "max", e.target.value === "1" ? 1 : undefined));
              flush();
            }}
          >
            <option value="1">1</option>
            <option value="*">*</option>
          </Select>
        </Field>
        <Field label="On delete" htmlFor={`${base}-ondelete`}>
          <Select
            id={`${base}-ondelete`}
            value={end.onDelete ?? "none"}
            onChange={(e) => {
              onChange((x) => setOptional(x as unknown as Rec, "onDelete", e.target.value === "none" ? undefined : e.target.value));
              flush();
            }}
          >
            {["none", "cascade", "restrict", "set-null"].map((v) => (
              <option key={v} value={v}>
                {v}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      <CheckboxField
        id={`${base}-ordered`}
        label="Ordered"
        checked={end.ordered === true}
        onChange={(v) => {
          onChange((x) => setOptional(x as unknown as Rec, "ordered", v ? true : undefined));
          flush();
        }}
      />
    </fieldset>
  );
}

export function RelationFields(props: FormProps & { withAttributes?: boolean }) {
  const { id, json, edit, flush, withAttributes = true } = props;
  const relation = json as RelationDoc;
  const vocab = useVocabularies("relation");
  const entities = vocab.lookup.ofKind("entity");
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-2 gap-2">
        <Field label="Kind" htmlFor={`${id}-relkind`}>
          <Select
            id={`${id}-relkind`}
            value={relation.relationKind ?? "association"}
            onChange={(e) => {
              edit((j) => setOptional(j as Rec, "relationKind", e.target.value === "association" ? undefined : e.target.value));
              flush();
            }}
          >
            {["association", "aggregation", "composition", "n-ary"].map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </Select>
        </Field>
        <TextField
          id={`${id}-inverse`}
          label="Inverse name"
          value={relation.inverseName ?? ""}
          onChange={(v) => edit((j) => setOptional(j as Rec, "inverseName", v))}
          onBlur={flush}
        />
      </div>
      {relation.ends.map((end, i) => (
        <EndEditor
          key={end.id}
          index={i}
          end={end}
          entities={entities}
          flush={flush}
          onChange={(update) =>
            edit((j) => {
              const r = j as RelationDoc;
              update(r.ends[i]);
            })
          }
        />
      ))}
      {withAttributes ? <AttributesOnlyFields {...props} /> : null}
    </div>
  );
}

export function EnumFields({ id, json, edit, flush }: FormProps) {
  const en = json as EnumDoc;
  const members = en.members ?? [];
  return (
    <div className="flex flex-col gap-2">
      <CheckboxField
        id={`${id}-flags`}
        label="Flags"
        checked={en.flags === true}
        onChange={(v) => {
          edit((j) => setOptional(j as Rec, "flags", v ? true : undefined));
          flush();
        }}
      />
      <SectionTitle
        actions={
          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              edit((j) => {
                const e = j as EnumDoc;
                e.members = [...(e.members ?? []), { id: newId(), name: `Member${(e.members?.length ?? 0) + 1}`, value: e.members?.length ?? 0 }];
              });
            }}
          >
            <Plus /> Add member
          </Button>
        }
      >
        Members
      </SectionTitle>
      <table className="w-full text-12" aria-label={`Members of ${en.name}`}>
        <thead>
          <tr className="text-left text-11 text-secondary">
            <th className="font-semibold">Name</th>
            <th className="font-semibold">Value</th>
            <th className="font-semibold">Code</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {members.map((m, i) => (
            <tr key={m.id}>
              <td className="pr-1">
                <Input
                  aria-label={`Name of member ${i + 1}`}
                  value={m.name}
                  className="h-7 text-12"
                  onChange={(e) => edit((j) => void ((j as EnumDoc).members![i].name = e.target.value))}
                  onBlur={flush}
                />
              </td>
              <td className="pr-1">
                <Input
                  aria-label={`Value of ${m.name}`}
                  value={m.value === undefined ? "" : String(m.value)}
                  className="h-7 w-16 text-right font-mono text-12"
                  onChange={(e) =>
                    edit((j) =>
                      setOptional((j as EnumDoc).members![i] as unknown as Rec, "value", /^-?\d+$/.test(e.target.value) ? Number(e.target.value) : undefined),
                    )
                  }
                  onBlur={flush}
                />
              </td>
              <td className="pr-1">
                <Input
                  aria-label={`Code of ${m.name}`}
                  value={m.code ?? ""}
                  className="h-7 w-16 text-12"
                  onChange={(e) => edit((j) => setOptional((j as EnumDoc).members![i] as unknown as Rec, "code", e.target.value))}
                  onBlur={flush}
                />
              </td>
              <td>
                <Button
                  size="icon-sm"
                  variant="ghost"
                  aria-label={`Remove member ${m.name}`}
                  onClick={() => {
                    edit((j) => void ((j as EnumDoc).members = (j as EnumDoc).members!.filter((x) => x.id !== m.id)));
                    flush();
                  }}
                >
                  <X />
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function ScalarFields({ id, json, edit, flush }: FormProps) {
  const s = json as ScalarTypeDoc;
  return (
    <div className="grid grid-cols-2 gap-2">
      <Field label="Base type" htmlFor={`${id}-base`}>
        <Select
          id={`${id}-base`}
          value={s.base}
          onChange={(e) => {
            edit((j) => void ((j as ScalarTypeDoc).base = e.target.value as ScalarTypeDoc["base"]));
            flush();
          }}
        >
          {BUILTIN_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </Select>
      </Field>
      {(["length", "precision", "scale"] as const).map((k) => (
        <TextField
          key={k}
          id={`${id}-${k}`}
          label={k[0].toUpperCase() + k.slice(1)}
          value={s[k] === undefined ? "" : String(s[k])}
          onChange={(v) => edit((j) => setOptional(j as Rec, k, /^\d+$/.test(v) ? Number(v) : undefined))}
          onBlur={flush}
        />
      ))}
    </div>
  );
}

export const DIALECTS = ["postgresql", "sqlserver", "sqlite", "mysql", "oracle"] as const;

export function DatabaseFields({ id, json, edit, flush }: FormProps) {
  const db = json as DatabaseDoc;
  return (
    <div className="grid grid-cols-2 gap-2">
      <Field label="Dialect" htmlFor={`${id}-dialect`}>
        <Select
          id={`${id}-dialect`}
          value={db.dialect}
          onChange={(e) => {
            edit((j) => void ((j as DatabaseDoc).dialect = e.target.value as DatabaseDoc["dialect"]));
            flush();
          }}
        >
          {DIALECTS.map((d) => (
            <option key={d} value={d}>
              {d}
            </option>
          ))}
        </Select>
      </Field>
      <TextField
        id={`${id}-version`}
        label="Version"
        value={db.version ?? ""}
        onChange={(v) => edit((j) => setOptional(j as Rec, "version", v))}
        onBlur={flush}
      />
      <TextField
        id={`${id}-schema`}
        label="Default schema"
        value={db.defaultSchema ?? ""}
        onChange={(v) => edit((j) => setOptional(j as Rec, "defaultSchema", v))}
        onBlur={flush}
        mono
      />
      <Field label="Quoting" htmlFor={`${id}-quoting`}>
        <Select
          id={`${id}-quoting`}
          value={db.quoting ?? "reserved"}
          onChange={(e) => {
            edit((j) => setOptional(j as Rec, "quoting", e.target.value === "reserved" ? undefined : e.target.value));
            flush();
          }}
        >
          {["reserved", "always", "never"].map((q) => (
            <option key={q} value={q}>
              {q}
            </option>
          ))}
        </Select>
      </Field>
    </div>
  );
}
