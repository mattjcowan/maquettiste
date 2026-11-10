// The Revisions tab of the entity editor (revisions-design.md section 12), part one: the Levels table, the separator, the attribute
// that holds the revision, and a live preview. The "When revised" tables (what a revise does to lifecycles, attributes and links)
// are part two and go where WhenRevised is. The entity's own block is what is written; levels from a base show "from <Base>" and
// are replaced as a whole the first time one is changed.
import { useEffect, useMemo, useState } from "react";
import { ChevronDown, ChevronUp, Plus, Trash2 } from "lucide-react";
import { useElements, useIndex } from "@/api/queries";
import type { AttributeDoc, EntityDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Field, Input, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { indexLookup } from "@/model/index";
import { subjectsOf } from "@/model/process";
import { governingLifecycles, resolveRevisions, type EntityWithRevisions, type RevisionsBlock } from "@/model/revisionSequence";
import { domIdOf, type EditorContext } from "./EditorFrame";
import { baseChain } from "./related";
import {
  addRevisions,
  heldInChoices,
  LEVEL_KIND_LABELS,
  levelsView,
  levelToRow,
  MAX_LEVELS,
  moved,
  newLevel,
  previewOf,
  rowToLevel,
  separatorChoices,
  sequenceOf,
  setOwnBoundAttribute,
  setOwnLevels,
  setOwnSeparator,
  type LevelKind,
  type LevelRow,
} from "./revisionsEdit";

type Rec = Record<string, unknown>;

export function EntityRevisionsTab({ id, json, edit, flush, diagnostics }: EditorContext) {
  const entity = json as EntityDoc;
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const bases = baseChain(lookup.byId, entity.base);
  const chain = [id, ...bases];
  const baseDocs = useElements(bases);
  const governing = useMemo(() => governingLifecycles(index.data ?? [], chain), [index.data, chain.join(",")]); // eslint-disable-line react-hooks/exhaustive-deps
  const lifecycleDocs = useElements(governing);
  const scalarDocs = useElements(lookup.ofKind("scalar-type").map((s) => s.id));

  const docOf = (at: string): EntityWithRevisions => (at === id ? entity : (baseDocs.byId.get(at)?.json as EntityWithRevisions));
  const merged = resolveRevisions(id, lookup.byId, docOf, governing);
  const own = (entity.revisions ?? undefined) as RevisionsBlock | undefined;
  const nameOf = (at: string | null) => (at ? (lookup.byId.get(at)?.name ?? at) : "");

  const commit = (update: (j: Rec) => void) => {
    edit((j) => {
      update(j as Rec);
    });
    flush();
  };

  if (!merged)
    return (
      <div className="flex items-center gap-2 text-13" data-testid="revisions-empty">
        <span className="text-secondary">{entity.name} has no revisions.</span>
        <Button size="sm" onClick={() => commit(addRevisions)} data-testid="revisions-add">
          <Plus /> Add revisions
        </Button>
      </div>
    );

  const dom = domIdOf(id);
  const { rows, inheritedFrom } = levelsView(merged, id);
  const setRows = (next: LevelRow[]) => commit((j) => setOwnLevels(j, next.map(rowToLevel)));
  const setRow = (i: number, patch: Partial<LevelRow>) => setRows(rows.map((r, at) => (at === i ? { ...r, ...patch } : r)));
  const separator = merged.separator;
  const preview = previewOf(sequenceOf(merged.levels, separator));
  const problems = diagnostics.filter((d) => /^\/revisions(\/|$)/.test(d.jsonPointer ?? ""));

  // Held in: the entity's attributes and its bases', the ones that fit decision 11.
  const attributes: AttributeDoc[] = chain.flatMap((at) => (docOf(at) as EntityDoc | undefined)?.attributes ?? []);
  const lifecycleBound = new Set(
    governing.flatMap((l) =>
      subjectsOf(lifecycleDocs.byId.get(l)?.json as Rec | undefined).flatMap((s) => (s.entity && chain.includes(s.entity) && s.boundAttribute ? [s.boundAttribute] : [])),
    ),
  );
  const scalarBase = (typeId: string) => (scalarDocs.byId.get(typeId)?.json as { base?: unknown } | undefined)?.base;
  const choices = heldInChoices(attributes, scalarBase, lifecycleBound);
  const held = merged.boundAttribute ?? "";
  const heldKnown = held === "" || choices.some((c) => c.id === held);

  return (
    <div className="flex flex-col gap-3" data-testid="editor-revisions">
      <section className="flex flex-col gap-1">
        <SectionTitle
          actions={
            <Button
              size="icon-row"
              label={rows.length >= MAX_LEVELS ? "A revision has at most three levels" : "Add level"}
              disabled={rows.length >= MAX_LEVELS}
              onClick={() => setRows([...rows, levelToRow(newLevel(rows.length))])}
              data-testid="revisions-add-level"
            >
              <Plus />
            </Button>
          }
        >
          Levels
        </SectionTitle>
        {inheritedFrom ? (
          <p className="text-12 text-secondary" data-testid="revisions-levels-from">
            from {nameOf(inheritedFrom)}: changing a level gives {entity.name} its own levels, which replace {nameOf(inheritedFrom)}&apos;s as a whole.
          </p>
        ) : null}
        {rows.length ? (
          <table className="w-full border-collapse text-13" data-testid="revisions-levels">
            <thead>
              <tr className="text-left text-11 uppercase tracking-wide text-secondary">
                <th className="w-6 px-1 font-semibold">#</th>
                <th className="px-1 font-semibold">Name</th>
                <th className="w-28 px-1 font-semibold">Values</th>
                <th className="w-20 px-1 font-semibold">First</th>
                <th className="w-24 px-1 font-semibold">Skip</th>
                <th className="w-16 px-1 font-semibold">Width</th>
                <th className="w-24 px-1 font-semibold">Starts empty</th>
                <th className="w-20 px-1" />
              </tr>
            </thead>
            <tbody>
              {rows.map((row, i) => (
                <tr key={i} data-testid={`revisions-level-${i + 1}`}>
                  <td className="px-1 text-secondary">{i + 1}</td>
                  <td className="p-0.5">
                    <CommitInput label={`Level ${i + 1} name`} value={row.name} onCommit={(v) => setRow(i, { name: v })} placeholder="major" />
                  </td>
                  <td className="p-0.5">
                    <Select
                      aria-label={`Level ${i + 1} values`}
                      value={row.kind}
                      onChange={(e) => setRow(i, { kind: e.target.value as LevelKind })}
                    >
                      {(Object.keys(LEVEL_KIND_LABELS) as LevelKind[]).map((k) => (
                        <option key={k} value={k}>
                          {LEVEL_KIND_LABELS[k]}
                        </option>
                      ))}
                    </Select>
                  </td>
                  {row.kind === "list" ? (
                    <td className="p-0.5" colSpan={3}>
                      <CommitInput
                        label={`Level ${i + 1} list values`}
                        value={row.values}
                        onCommit={(v) => setRow(i, { values: v })}
                        placeholder="Prototype, Pilot run, Production"
                        title="The values in order, separated by commas. The level ends at the last one."
                      />
                    </td>
                  ) : (
                    <>
                      <td className="p-0.5">
                        <CommitInput
                          label={`Level ${i + 1} first value`}
                          value={row.first}
                          onCommit={(v) => setRow(i, { first: v })}
                          placeholder={row.kind === "numbers" ? "1" : "A"}
                        />
                      </td>
                      <td className="p-0.5">
                        <CommitInput
                          label={`Level ${i + 1} skipped letters`}
                          value={row.skip}
                          onCommit={(v) => setRow(i, { skip: v })}
                          disabled={row.kind !== "letters"}
                          placeholder={row.kind === "letters" ? "I, O" : ""}
                          title="Letters never used, at any position"
                        />
                      </td>
                      <td className="p-0.5">
                        <CommitInput
                          label={`Level ${i + 1} width`}
                          value={row.width}
                          onCommit={(v) => setRow(i, { width: v })}
                          disabled={row.kind !== "numbers"}
                          placeholder={row.kind === "numbers" ? "2" : ""}
                          title="Numbers are padded with zeros to this width"
                        />
                      </td>
                    </>
                  )}
                  <td className="px-1">
                    <Checkbox
                      id={`${dom}-revisions-empty-${i}`}
                      aria-label={`Level ${i + 1} starts empty`}
                      title="The level holds nothing at first, and again whenever a higher level moves"
                      checked={row.startEmpty}
                      onCheckedChange={(v) => setRow(i, { startEmpty: v === true })}
                    />
                  </td>
                  <td className="px-1">
                    <span className="flex items-center gap-0.5">
                      <Button size="icon-row" variant="ghost" label="Move level up" disabled={i === 0} onClick={() => setRows(moved(rows, i, -1))}>
                        <ChevronUp />
                      </Button>
                      <Button
                        size="icon-row"
                        variant="ghost"
                        label="Move level down"
                        disabled={i === rows.length - 1}
                        onClick={() => setRows(moved(rows, i, 1))}
                      >
                        <ChevronDown />
                      </Button>
                      <Button size="icon-row" variant="ghost" label="Remove level" onClick={() => setRows(rows.filter((_, at) => at !== i))}>
                        <Trash2 />
                      </Button>
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : (
          <p className="text-12 text-secondary">No levels yet. Add one to say what a revision looks like.</p>
        )}
      </section>

      <div className="grid max-w-xl grid-cols-2 gap-2">
        <Field label="Separator" htmlFor={`${dom}-revisions-separator`} hint="Written between two levels that both hold a value.">
          <Select
            id={`${dom}-revisions-separator`}
            value={own?.separator === undefined ? "default" : `sep:${own.separator}`}
            onChange={(e) => commit((j) => setOwnSeparator(j, e.target.value === "default" ? undefined : e.target.value.slice(4)))}
          >
            <option value="default">
              {merged.separatorFrom && merged.separatorFrom !== id
                ? `Same as ${nameOf(merged.separatorFrom)} ("${merged.separator}")`
                : `Default (".")`}
            </option>
            {separatorChoices(own?.separator).map((c) => (
              <option key={c.value} value={`sep:${c.value}`}>
                {c.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field
          label="Held in"
          htmlFor={`${dom}-revisions-held`}
          hint={
            merged.boundAttributeFrom && merged.boundAttributeFrom !== id
              ? `from ${nameOf(merged.boundAttributeFrom)}`
              : "The attribute that holds the current revision. Without one, your own system holds it."
          }
        >
          <Select
            id={`${dom}-revisions-held`}
            value={held}
            onChange={(e) => commit((j) => setOwnBoundAttribute(j, e.target.value || undefined))}
          >
            <option value="">Not held in an attribute</option>
            {heldKnown ? null : <option value={held}>(an attribute that does not fit)</option>}
            {choices.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </Select>
        </Field>
      </div>

      <section className="flex flex-col gap-1" data-testid="revisions-preview">
        <SectionTitle>Preview</SectionTitle>
        {preview.ok ? (
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-13">
            <dt className="text-secondary">First revision</dt>
            <dd className="font-mono" data-testid="revisions-preview-first">
              {preview.first === "" ? "none (a new object has no revision)" : preview.first}
            </dd>
            {preview.runs.map((r) => (
              <PreviewLine key={r.level} label={r.label} text={r.text} level={r.level} />
            ))}
          </dl>
        ) : (
          <p className="text-12 text-secondary">Add a level to see the revisions it gives.</p>
        )}
      </section>

      {problems.length ? (
        <ul className="text-12 text-danger" role="alert" data-testid="revisions-findings">
          {problems.map((d, i) => (
            <li key={i}>
              {d.rule}: {d.message}
            </li>
          ))}
        </ul>
      ) : null}

      <WhenRevised />
    </div>
  );
}

function PreviewLine({ label, text, level }: { label: string; text: string; level: number }) {
  return (
    <>
      <dt className="text-secondary">{label}</dt>
      <dd className="font-mono" data-testid={`revisions-preview-level-${level}`}>
        {text}
      </dd>
    </>
  );
}

/** Part two: the "When revised" tables (a row per lifecycle that governs the entity, and the Attributes and Links tables) go here. */
function WhenRevised() {
  return null;
}

/** A text field that writes when it loses focus or Enter is pressed, and follows the file when it changes elsewhere. */
function CommitInput({
  label,
  value,
  onCommit,
  ...props
}: { label: string; value: string; onCommit: (value: string) => void } & Omit<React.InputHTMLAttributes<HTMLInputElement>, "value" | "onChange">) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  const done = () => {
    if (text !== value) onCommit(text);
  };
  return (
    <Input
      {...props}
      aria-label={label}
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={done}
      onKeyDown={(e) => {
        if (e.key === "Enter") done();
      }}
    />
  );
}
