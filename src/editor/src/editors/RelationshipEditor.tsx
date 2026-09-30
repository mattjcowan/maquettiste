// The relationship editor (explorer-redesign.md 3.6): name and domain, the ends (entity, role, cardinality, delete
// behaviour) and the mark chips on top; tabs Attributes, Mappings, Code generation (when an extension schema applies)
// and References. On the same frame as the entity editor.
import { useQueries, useQueryClient } from "@tanstack/react-query";
import { tablesQuery, useElements, useIndex } from "@/api/queries";
import { useEditorNavigation } from "@/app/navigation";
import { SectionTitle } from "@/components/ui/misc";
import { indexLookup } from "@/model/index";
import { ElementLink } from "./EntityEditor";
import { relationMappingRows, setRelationShape, type RelationMappingLike, type RelationShape } from "./inheritance";
import { useElementEdits } from "./mappingEdit";
import { Field, Input, Select } from "@/components/ui/input";
import { newId } from "@/lib/ids";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { References } from "@/inspector/Inspector";
import type { RelationDoc } from "@/api/types";
import { SeedDataTab } from "./SeedDataTab";
import { AttributesOnlyFields, RelationFields } from "@/inspector/fields";
import { domIdOf, EditorLayout, MarkChips, NameAndDomain, useCodeGenerationTab, useEditorContext, type EditorContext } from "./EditorFrame";

export function RelationshipEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "relation");
  if (!ctx) return <>{fallback}</>;
  return <RelationshipBody ctx={ctx} draft={draft} />;
}

function RelationshipBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const index = useIndex();
  const seeded = ((ctx.json as RelationDoc).attributes ?? []).length > 0 || (index.data ?? []).some((r) => r.kind === "seed" && r.target === ctx.id);
  // The shared forms use `id` only for their field ids: give them the editor's prefix.
  const form = { ...ctx, id: domIdOf(ctx.id) };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={
        <div className="flex flex-col gap-2">
          <NameAndDomain {...ctx} />
          <RelationFields {...form} withAttributes={false} />
          <MarkChips {...ctx} />
        </div>
      }
      tabs={[
        { value: "attributes", label: EDITOR_TAB_LABELS.attributes, content: <AttributesOnlyFields {...form} /> },
        { value: "mappings", label: EDITOR_TAB_LABELS.mappings, content: <RelationMappingsTab id={ctx.id} /> },
        // A relation's own rows: its links, with the relation's attributes (the links of a relation without
        // attributes are the end columns of its entities' seeds).
        ...(seeded ? [{ value: "seed-data", label: EDITOR_TAB_LABELS.seedData, content: <SeedDataTab id={ctx.id} />, fill: true }] : []),
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}

const SHAPE_LABELS: Record<RelationShape, string> = {
  "foreign-key": "Foreign key",
  junction: "Junction table",
  promoted: "Promoted to an entity",
};

/** Per database: the relation's customised mapping (shape, foreign key end) and the tables resolved for it. */
function RelationMappingsTab({ id }: { id: string }) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const { openWorkspace, select } = useEditorNavigation();
  const databases = lookup.ofKind("database").map((d) => ({ id: d.id, name: d.name }));
  // A relation's mapping has no entity in the index: read those few documents and keep the ones for this relation.
  const candidates = lookup.ofKind("mapping").filter((m) => !m.entity);
  const docs = useElements(candidates.map((m) => m.id));
  const mappings = candidates.map((m) => ({ ...((docs.byId.get(m.id)?.json as RelationMappingLike | undefined) ?? { database: m.database }), id: m.id }));
  const qc = useQueryClient();
  // The same queries as the Databases explorer's (["tables", id]), so an open explorer has them cached already.
  const tables = useQueries({ queries: databases.map((d) => tablesQuery(qc, d.id)) });
  const rows = relationMappingRows(id, databases, mappings, (db) => tables[databases.findIndex((d) => d.id === db)]?.data?.tables);
  const edits = useElementEdits();
  const relationName = lookup.byId.get(id)?.name ?? "relationship";
  return (
    <div className="flex flex-col gap-2" data-testid="editor-relation-mappings">
      <p className="text-12 text-secondary">
        Each database stores the relation by its conventions unless a mapping overrides them.{" "}
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
      {rows.length ? null : <p className="text-12 text-secondary">No database yet.</p>}
      {rows.map((r) => {
        const summary = r.mapping ? lookup.byId.get(r.mapping.id) : undefined;
        const designed = lookup.ofKind("table").filter((t) => t.database === r.database);
        const change = (patch: Parameters<typeof setRelationShape>[1]) => {
          if (r.mapping) void edits.update(r.mapping.id, (j) => setRelationShape(j, patch));
          else {
            const json: Record<string, unknown> = {
              kind: "mapping",
              id: newId(),
              name: `${relationName} in ${r.databaseName}`,
              database: r.database,
              relation: id,
            };
            setRelationShape(json, patch);
            void edits.create(json, `Map ${relationName} in ${r.databaseName}`);
          }
        };
        return (
          <section key={r.database} className="flex flex-col gap-1" data-testid="relation-mapping-database">
            <SectionTitle>{r.databaseName}</SectionTitle>
            {r.ignored ? <p className="text-13">Left out of this database.</p> : null}
            <div className="flex flex-wrap items-end gap-2">
              <Field label="Shape" htmlFor={`${id}-${r.database}-shape`}>
                <Select
                  id={`${id}-${r.database}-shape`}
                  aria-label={`Shape in ${r.databaseName}`}
                  className="w-56"
                  value={r.shape ?? ""}
                  disabled={r.ignored}
                  onChange={(e) => change({ shape: (e.target.value || null) as RelationShape | null })}
                >
                  <option value="">By convention</option>
                  {(Object.keys(SHAPE_LABELS) as RelationShape[]).map((k) => (
                    <option key={k} value={k}>
                      {SHAPE_LABELS[k]}
                    </option>
                  ))}
                </Select>
              </Field>
              {r.shape === "junction" ? (
                <Field label="Junction table" htmlFor={`${id}-${r.database}-junction`}>
                  <Select
                    id={`${id}-${r.database}-junction`}
                    aria-label={`Junction table in ${r.databaseName}`}
                    className="w-56"
                    value={(r.mapping as { junctionTable?: string } | null)?.junctionTable ?? ""}
                    onChange={(e) => change({ junctionTable: e.target.value || null })}
                  >
                    <option value="">Named by the conventions</option>
                    {designed.map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.name}
                      </option>
                    ))}
                  </Select>
                </Field>
              ) : null}
              {r.shape === "promoted" ? (
                <Field label="Promoted entity name" htmlFor={`${id}-${r.database}-promoted`}>
                  <Input
                    id={`${id}-${r.database}-promoted`}
                    className="w-56"
                    key={r.mapping?.promotedName ?? ""}
                    defaultValue={r.mapping?.promotedName ?? ""}
                    placeholder="Named by the conventions"
                    onBlur={(e) => {
                      if (e.target.value.trim() !== (r.mapping?.promotedName ?? "")) change({ promotedName: e.target.value });
                    }}
                  />
                </Field>
              ) : null}
            </div>
            {summary ? <ElementLink summary={summary} secondary="customised mapping" pin={false} /> : null}
            {r.tables.map((t) => {
              const table = lookup.byId.get(t.key);
              return table ? (
                <ElementLink key={t.key} summary={table} secondary="table" pin={false} />
              ) : (
                <p key={t.key} className="font-mono text-12 text-secondary">
                  {t.name}
                </p>
              );
            })}
          </section>
        );
      })}
    </div>
  );
}
