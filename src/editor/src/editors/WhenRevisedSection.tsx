// "When revised" in the entity editor's Revisions tab (revisions-design.md section 12): a row per lifecycle that governs the entity (start
// over, keep, or go to one of its states), and the Attributes and Links tables. A row a base wrote shows "from <Base>" and is replaced by
// choosing another value, which writes the entity's own entry; an own entry has a remove button, which lets the base's (or the fallback) apply.
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { stateChoices } from "@/model/effects";
import type { ProcessDoc } from "@/model/process";
import type { MergedRevisions, RevisionsBlock } from "@/model/revisionSequence";
import {
  ATTRIBUTE_RULE_LABELS,
  attributeChoices,
  attributeRows,
  hasOwnEntry,
  LIFECYCLE_RULE_LABELS,
  LINK_RULE_LABELS,
  lifecycleRows,
  linkRows,
  removeOwnEntry,
  setOwnEntry,
  type AttributeChoice,
  type AttributeRule,
  type EndChoice,
  type EntryRow,
  type LinkRule,
} from "./whenRevised";

type Rec = Record<string, unknown>;

export function WhenRevisedSection({
  entityId,
  merged,
  own,
  commit,
  nameOf,
  lifecycleNames,
  lifecycleDocs,
  attributes,
  keyIds,
  lifecycleBound,
  ends,
}: {
  entityId: string;
  merged: MergedRevisions;
  own: RevisionsBlock | undefined;
  commit: (update: (json: Rec) => void) => void;
  nameOf: (id: string | null) => string;
  lifecycleNames: ReadonlyMap<string, string>;
  lifecycleDocs: readonly ProcessDoc[];
  attributes: readonly (AttributeChoice & { id: string })[];
  keyIds: readonly string[];
  lifecycleBound: ReadonlySet<string>;
  ends: readonly EndChoice[];
}) {
  const lifecycles = lifecycleRows(merged);
  const states = new Map(stateChoices(lifecycleDocs).flatMap((g) => g.states.map((s) => [s.id, s.label] as const)));
  const attributeNames = new Map(attributes.map((a) => [a.id, a.name]));
  const endLabels = new Map(ends.map((e) => [e.id, e.label]));
  const attrRows = attributeRows(merged, own, entityId);
  const links = linkRows(merged);
  const addable = attributeChoices(attributes, { key: keyIds, lifecycleBound, held: merged.boundAttribute, listed: attrRows.map((r) => r.id) });
  const addableEnds = ends.filter((e) => !links.some((l) => l.id === e.id));

  const fromCell = (from: string | null) => <td className="px-1 text-12 text-secondary">{from && from !== entityId ? `from ${nameOf(from)}` : ""}</td>;
  const removeCell = (key: "lifecycles" | "attributes" | "links", id: string, what: string) => (
    <td className="w-8 px-1">
      {hasOwnEntry(own, key, id) ? (
        <Button size="icon-row" variant="ghost" label={`Remove ${what}`} onClick={() => commit((j) => removeOwnEntry(j, key, id))}>
          <Trash2 />
        </Button>
      ) : null}
    </td>
  );
  const head = (cols: string[]) => (
    <thead>
      <tr className="text-left text-11 uppercase tracking-wide text-secondary">
        {cols.map((c, i) => (
          <th key={i} className="px-1 font-semibold">
            {c}
          </th>
        ))}
        <th className="w-8" />
      </tr>
    </thead>
  );

  return (
    <section className="flex flex-col gap-2" data-testid="revisions-when-revised">
      <SectionTitle>When revised</SectionTitle>

      <div className="flex flex-col gap-1" data-testid="revisions-lifecycles">
        {lifecycles.length ? (
          <table className="w-full max-w-2xl border-collapse text-13">
            {head(["Lifecycle", "What it does", ""])}
            <tbody>
              {lifecycles.map((l) => (
                <tr key={l.id} data-testid={`revisions-lifecycle-${lifecycleNames.get(l.id) ?? l.id}`}>
                  <td className="px-1">{lifecycleNames.get(l.id) ?? l.id}</td>
                  <td className="p-0.5">
                    <Select
                      aria-label={`${lifecycleNames.get(l.id) ?? "Lifecycle"} when revised`}
                      value={l.rule}
                      onChange={(e) => commit((j) => setOwnEntry(j, "lifecycles", l.id, e.target.value))}
                    >
                      <option value="restart">{LIFECYCLE_RULE_LABELS.restart}</option>
                      <option value="keep">{LIFECYCLE_RULE_LABELS.keep}</option>
                      {stateChoices(lifecycleDocs.filter((d) => d.id === l.id)).flatMap((g) =>
                        g.states.map((st) => (
                          <option key={st.id} value={st.id}>
                            Go to {st.label.slice(g.lifecycle.length + 1)}
                          </option>
                        )),
                      )}
                      {l.rule !== "restart" && l.rule !== "keep" && !states.has(l.rule) ? <option value={l.rule}>(a state that is gone)</option> : null}
                    </Select>
                  </td>
                  {fromCell(l.from)}
                  {removeCell("lifecycles", l.id, "this row (starts over again)")}
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <p className="text-12 text-secondary">No lifecycle governs this type, so a revise has no states to move.</p>
        )}
      </div>

      <EntryTable
        title="Attributes"
        empty="Every attribute is copied to a new revision. Add one to reset or clear it."
        testId="revisions-attributes"
        rows={attrRows}
        labelOf={(id) => attributeNames.get(id) ?? "(an attribute that is gone)"}
        options={(Object.keys(ATTRIBUTE_RULE_LABELS) as AttributeRule[]).map((r) => ({ value: r, label: ATTRIBUTE_RULE_LABELS[r] }))}
        entityId={entityId}
        nameOf={nameOf}
        addLabel="Add attribute"
        choices={addable.map((a) => ({ id: a.id, label: a.name }))}
        onAdd={(id) => commit((j) => setOwnEntry(j, "attributes", id, "reset"))}
        onSet={(id, rule) => commit((j) => setOwnEntry(j, "attributes", id, rule))}
        onRemove={(id) => commit((j) => removeOwnEntry(j, "attributes", id))}
        isOwn={(id) => hasOwnEntry(own, "attributes", id)}
        column="Attribute"
      />
      <EntryTable
        title="Links"
        empty="A link is left to your own system. Add one to say what happens to it."
        testId="revisions-links"
        rows={links}
        labelOf={(id) => endLabels.get(id) ?? "(an end that is gone)"}
        options={(Object.keys(LINK_RULE_LABELS) as LinkRule[]).map((r) => ({ value: r, label: LINK_RULE_LABELS[r] }))}
        entityId={entityId}
        nameOf={nameOf}
        addLabel="Add link"
        choices={addableEnds}
        onAdd={(id) => commit((j) => setOwnEntry(j, "links", id, "stay"))}
        onSet={(id, rule) => commit((j) => setOwnEntry(j, "links", id, rule))}
        onRemove={(id) => commit((j) => removeOwnEntry(j, "links", id))}
        isOwn={(id) => hasOwnEntry(own, "links", id)}
        column="Link"
      />
    </section>
  );
}

function EntryTable<R extends string>({
  title,
  empty,
  testId,
  rows,
  labelOf,
  options,
  entityId,
  nameOf,
  addLabel,
  choices,
  onAdd,
  onSet,
  onRemove,
  isOwn,
  column,
}: {
  title: string;
  empty: string;
  testId: string;
  rows: readonly EntryRow<R>[];
  labelOf: (id: string) => string;
  options: { value: R; label: string }[];
  entityId: string;
  nameOf: (id: string | null) => string;
  addLabel: string;
  choices: readonly { id: string; label: string }[];
  onAdd: (id: string) => void;
  onSet: (id: string, rule: R) => void;
  onRemove: (id: string) => void;
  isOwn: (id: string) => boolean;
  column: string;
}) {
  return (
    <div className="flex flex-col gap-1" data-testid={testId}>
      <div className="flex items-center gap-2">
        <h4 className="text-11 font-semibold uppercase tracking-wide text-secondary">{title}</h4>
        <Select
          className="h-6 w-44 text-12"
          aria-label={addLabel}
          title={choices.length ? addLabel : "Nothing left to add"}
          disabled={!choices.length}
          value=""
          onChange={(e) => e.target.value && onAdd(e.target.value)}
        >
          <option value="">{`+ ${addLabel}`}</option>
          {choices.map((c) => (
            <option key={c.id} value={c.id}>
              {c.label}
            </option>
          ))}
        </Select>
      </div>
      {rows.length ? (
        <table className="w-full max-w-2xl border-collapse text-13">
          <thead>
            <tr className="text-left text-11 uppercase tracking-wide text-secondary">
              <th className="px-1 font-semibold">{column}</th>
              <th className="px-1 font-semibold">When revised</th>
              <th />
              <th className="w-8" />
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} data-testid={`${testId}-row`}>
                <td className="px-1">{labelOf(r.id)}</td>
                <td className="p-0.5">
                  <Select aria-label={`${labelOf(r.id)} when revised`} value={r.rule} onChange={(e) => onSet(r.id, e.target.value as R)}>
                    {options.map((o) => (
                      <option key={o.value} value={o.value}>
                        {o.label}
                      </option>
                    ))}
                  </Select>
                </td>
                <td className="px-1 text-12 text-secondary">{r.from && r.from !== entityId ? `from ${nameOf(r.from)}` : ""}</td>
                <td className="w-8 px-1">
                  {isOwn(r.id) ? (
                    <Button size="icon-row" variant="ghost" label={`Remove ${labelOf(r.id)}`} onClick={() => onRemove(r.id)}>
                      <Trash2 />
                    </Button>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p className="text-12 text-secondary">{empty}</p>
      )}
    </div>
  );
}
