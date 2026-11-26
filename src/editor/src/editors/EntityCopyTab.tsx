// The Copy tab of the entity editor (parts-and-copy-design.md section 3): what a copy of a whole record carries over. Fields (each attribute),
// Links (each to-one end the entity holds) and Parts (the entity's compositions as a tree). The entity's own `copy` block is what is written;
// an entry a base wrote shows "from <Base>" and is replaced by choosing another word (and taken back with "Back to base"). The parts are worked
// out here from the relations that reference each entity on the way down (one where-used call per entity, never a read of the whole model),
// the way the engine's `entity.parts` lists them.
import { useEffect, useMemo, useState } from "react";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import { loadElement, useElements, useIndex } from "@/api/queries";
import type { ElementSummary, EntityDoc } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { indexLookup } from "@/model/index";
import { stateChoices } from "@/model/effects";
import type { ProcessDoc } from "@/model/process";
import type { EditorContext } from "./EditorFrame";
import { baseChain } from "./related";
import { referenceReader, referrersOf, WALK_LIMITS } from "./refWalk";
import { ValueBox } from "./ValueBox";
import {
  ATTRIBUTE_WORDS,
  choiceOf,
  copyAttributes,
  hasOwnCopyEntry,
  inheritedEntries,
  isOrderable,
  LINK_WORDS,
  linkEnds,
  mergedEntries,
  partTree,
  PART_WORDS,
  printCondition,
  removeOwnCopyEntry,
  removeOwnPart,
  setCopyWord,
  setPartAttributeWord,
  setPartChoice,
  setPartWord,
  type AttributeLike,
  type Choice,
  type Json,
  type PartInfo,
  type PartWord,
} from "./copyEdit";

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

/** What the walk down the compositions found: the relations that touch each entity on the way, and their documents. */
interface RelationWalk {
  docs: Map<string, Json>;
  relatedIds: Map<string, string[]>;
  /** Whether WALK_LIMITS cut the walk. */
  truncated: boolean;
}

/** The ids of the custom types the attributes of some entities use. */
function typeRefsOf(docs: readonly Json[]): string[] {
  const out = new Set<string>();
  for (const d of docs)
    for (const a of arr(d.attributes)) {
      const t = a.type as { ref?: unknown } | string | undefined;
      if (t && typeof t === "object" && typeof t.ref === "string") out.add(t.ref);
    }
  return [...out].sort();
}

export function EntityCopyTab(ctx: EditorContext) {
  const { id, json } = ctx;
  const entity = json as EntityDoc;
  const index = useIndex();
  const lookup = useMemo(() => indexLookup(index.data), [index.data]);
  const baseOf = (at: string) => lookup.byId.get(at)?.base ?? undefined;
  const bases = baseChain(lookup.byId, entity.base);
  const baseDocs = useElements(bases);
  const chain = useMemo<Json[]>(
    () => [json as Json, ...bases.flatMap((b) => (baseDocs.byId.get(b)?.json ? [baseDocs.byId.get(b)!.json as Json] : []))],
    [json, bases.join(","), baseDocs.byId], // eslint-disable-line react-hooks/exhaustive-deps
  );
  // Until every base is loaded, what a base wrote is unknown: nothing is edited meanwhile.
  const locked = bases.some((b) => !baseDocs.byId.has(b));
  const nameOf = (at: string | null | undefined) => (at ? (lookup.byId.get(at)?.name ?? at) : "");

  // The relations that touch an entity (it or a base) are the ones that reference it: asked one entity at a time, level by level down the
  // compositions (the calls of a level run together), never from the whole index. The walk is cut at WALK_LIMITS and says so. It runs again
  // whenever the index refreshes (an element was saved), keeping the tree it had meanwhile.
  const qc = useQueryClient();
  const kindOf = (x: string) => lookup.byId.get(x)?.kind;
  const relations = useQuery({
    queryKey: ["copy-relations", id, bases.join(","), index.dataUpdatedAt],
    placeholderData: keepPreviousData,
    queryFn: async (): Promise<RelationWalk> => {
      const read = referenceReader(qc);
      const docs = new Map<string, Json>();
      const relatedIds = new Map<string, string[]>();
      const rowsOf = (e: string) => (relatedIds.get(e) ?? []).flatMap((r) => (lookup.byId.get(r) ? [lookup.byId.get(r)!] : []));
      let truncated = false;
      for (let round = 0; round <= WALK_LIMITS.depth; round++) {
        const tree = partTree(id, rowsOf, docs, baseOf);
        let todo = [...new Set([id, ...tree.map((p) => p.entity)])].filter((e) => !relatedIds.has(e));
        if (!todo.length) break;
        if (round === WALK_LIMITS.depth) {
          truncated = true;
          break;
        }
        if (relatedIds.size + todo.length > WALK_LIMITS.entities) {
          truncated = true;
          todo = todo.slice(0, Math.max(0, WALK_LIMITS.entities - relatedIds.size));
          if (!todo.length) break;
        }
        await Promise.all(
          todo.map(async (e) => {
            const chainIds: string[] = [];
            for (let c: string | undefined = e; c && !chainIds.includes(c); c = baseOf(c)) chainIds.push(c);
            const refs = await Promise.all(chainIds.map(read));
            relatedIds.set(e, [...new Set(refs.flatMap((r) => referrersOf(r, kindOf, "relation")))]);
          }),
        );
        let wanted = [...new Set([...relatedIds.values()].flat())].filter((r) => !docs.has(r));
        if (docs.size + wanted.length > WALK_LIMITS.relations) {
          truncated = true;
          wanted = wanted.slice(0, Math.max(0, WALK_LIMITS.relations - docs.size));
        }
        for (const doc of await Promise.all(wanted.map((r) => loadElement(r)))) docs.set(String(doc.json.id), doc.json as Json);
      }
      return { docs, relatedIds, truncated };
    },
    enabled: !index.isPending && lookup.byId.has(id),
  });
  const walk = relations.data;
  const relationDocs = walk?.docs ?? new Map<string, Json>();
  const related = (at: string): ElementSummary[] => (walk?.relatedIds.get(at) ?? []).flatMap((r) => (lookup.byId.get(r) ? [lookup.byId.get(r)!] : []));
  const parts = useMemo(() => partTree(id, related, relationDocs, baseOf), [walk, index.data, id]); // eslint-disable-line react-hooks/exhaustive-deps

  // The documents of every part entity and its bases (attributes, order), the lifecycles that govern them, and the custom types the
  // attributes use (which give an attribute its order and what `original` may hold): only what the entity's and the parts' own attributes name.
  const partChains = useMemo(
    () => new Map(parts.map((p) => [p.entity, [p.entity, ...baseChain(lookup.byId, lookup.byId.get(p.entity)?.base)]] as const)),
    [parts, lookup],
  );
  const partDocIds = useMemo(() => [...new Set([...partChains.values()].flat())].sort(), [partChains]);
  const partDocs = useElements(partDocIds);
  // A lifecycle governs a part when its index row names the part entity or a base as a subject, or covers it; its referrers are the candidates.
  const governingQuery = useQuery({
    queryKey: ["copy-governing", partDocIds.join(","), index.dataUpdatedAt],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const read = referenceReader(qc);
      const out = new Map<string, string[]>();
      await Promise.all(
        [...partChains].map(async ([e, c]) => {
          const found = (await Promise.all(c.map(read))).flatMap((r) => referrersOf(r, kindOf, "process"));
          const governs = (p: string) => {
            const row = lookup.byId.get(p);
            const named = [...(row?.subject ? [row.subject] : []), ...(row?.subjects ?? []), ...(row?.covers ?? [])];
            return row?.use === "lifecycle" && c.some((x) => named.includes(x));
          };
          out.set(e, [...new Set(found.filter(governs))]);
        }),
      );
      return out;
    },
    enabled: partDocIds.length > 0,
  });
  const governing = useMemo(() => governingQuery.data ?? new Map<string, string[]>(), [governingQuery.data]);
  const lifecycleDocs = useElements(useMemo(() => [...new Set([...governing.values()].flat())].sort(), [governing]));
  const typeIds = useMemo(() => typeRefsOf([...chain, ...[...partDocs.byId.values()].map((d) => d.json as Json)]), [chain, partDocs.byId]);
  const typeDocs = useElements(typeIds);
  const typeBaseIds = useMemo(
    () =>
      [...typeDocs.byId.values()].flatMap((d) =>
        typeof (d.json as Json).base === "string" && lookup.byId.has((d.json as Json).base as string) ? [(d.json as Json).base as string] : [],
      ),
    [typeDocs.byId, lookup],
  );
  const typeBaseDocs = useElements(typeBaseIds);
  const scalarBase = (typeId: string) => ((typeDocs.byId.get(typeId) ?? typeBaseDocs.byId.get(typeId))?.json as { base?: unknown } | undefined)?.base;
  // The names an enum's members go by in a condition.
  const memberNames = useMemo(() => {
    const out = new Map<string, string>();
    for (const d of typeDocs.byId.values()) for (const m of arr((d.json as Json).members)) out.set(String(m.id), String(m.name));
    return out;
  }, [typeDocs.byId]);

  const commit = (update: (j: Json) => void) => {
    ctx.edit((j) => void update(j as Json));
    ctx.flush();
  };
  const problems = ctx.diagnostics.filter((d) => /^\/copy(\/|$)/.test(d.jsonPointer ?? ""));
  const base = (at: string | null | undefined) => nameOf(at);

  // Fields
  const attributes = copyAttributes(chain, scalarBase);
  const attributeEntries = mergedEntries(chain, "attributes", "attribute");
  const attributeBase = inheritedEntries(chain, "attributes", "attribute");
  // Links
  const ends = linkEnds(id, related(id), relationDocs, baseOf);
  const linkMerged = mergedEntries(chain, "links", "end");
  const linkBase = inheritedEntries(chain, "links", "end");
  // Parts
  const partMerged = mergedEntries(chain, "parts", "end");
  const partBase = inheritedEntries(chain, "parts", "end");

  const fromCell = (from: string | null, key: "attributes" | "links", entryId: string, inherited: boolean) => (
    <td className="w-48 px-1 text-12 text-secondary">
      {from ? `from ${base(from)}` : ""}
      {!from && inherited && hasOwnCopyEntry(json as Json, key, entryId) ? (
        <Button size="sm" variant="ghost" disabled={locked} onClick={() => commit((j) => removeOwnCopyEntry(j, key, entryId))} data-testid="copy-back-to-base">
          Back to base
        </Button>
      ) : null}
    </td>
  );

  return (
    <div className="flex max-w-3xl flex-col gap-3" data-testid="editor-copy">
      <p className="text-12 text-secondary">
        What a copy of {entity.name} carries over. Anything not listed is copied as it is; a new copy always gets a new key and starts every lifecycle at its
        initial state. Parts reached through an owner end that names a stereotype or any entity, and attributes a stereotype adds, are not listed here: edit
        them on the JSON tab.
      </p>

      <section className="flex flex-col gap-1" data-testid="copy-fields">
        <SectionTitle>{`Fields (${attributes.length})`}</SectionTitle>
        {attributes.length ? (
          <table className="w-full border-collapse text-13">
            <tbody>
              {attributes.map((a) => {
                const merged = attributeEntries.get(a.id);
                const word = String(merged?.entry.onCopy ?? "copy");
                const from = merged && merged.from !== id ? merged.from : null;
                return (
                  <tr key={a.id} className={cn(from && "text-secondary")} data-testid="copy-field-row" data-name={a.name} data-from={from ? "base" : "own"}>
                    <td className="px-1">{a.name}</td>
                    <td className="w-44 p-0.5">
                      <WordSelect
                        label={`${a.name} when copied`}
                        value={word}
                        words={a.words}
                        labels={ATTRIBUTE_WORDS}
                        disabled={locked}
                        onChange={(w) => commit((j) => setCopyWord(j, "attributes", a.id, w, attributeBase.get(a.id)?.entry.onCopy as string | undefined))}
                      />
                    </td>
                    {fromCell(from, "attributes", a.id, attributeBase.has(a.id))}
                  </tr>
                );
              })}
            </tbody>
          </table>
        ) : (
          <p className="text-12 text-secondary">{entity.name} has no attribute a copy could change (a key or a derived attribute is never listed).</p>
        )}
      </section>

      <section className="flex flex-col gap-1" data-testid="copy-links">
        <SectionTitle>{`Links (${ends.length})`}</SectionTitle>
        {ends.length ? (
          <table className="w-full border-collapse text-13">
            <tbody>
              {ends.map((e) => {
                const merged = linkMerged.get(e.id);
                const word = String(merged?.entry.onCopy ?? "copy");
                const from = merged && merged.from !== id ? merged.from : null;
                return (
                  <tr key={e.id} className={cn(from && "text-secondary")} data-testid="copy-link-row" data-name={e.label}>
                    <td className="px-1">{e.label}</td>
                    <td className="w-44 p-0.5">
                      <WordSelect
                        label={`${e.label} when copied`}
                        value={word}
                        words={e.original ? ["copy", "original"] : ["copy"]}
                        labels={LINK_WORDS}
                        disabled={locked}
                        onChange={(w) => commit((j) => setCopyWord(j, "links", e.id, w, linkBase.get(e.id)?.entry.onCopy as string | undefined))}
                      />
                    </td>
                    {fromCell(from, "links", e.id, linkBase.has(e.id))}
                  </tr>
                );
              })}
            </tbody>
          </table>
        ) : (
          <p className="text-12 text-secondary">No to-one link of {entity.name} that a copy could point back at its original.</p>
        )}
      </section>

      <section className="flex flex-col gap-1" data-testid="copy-parts">
        <SectionTitle>{`Parts (${parts.length})`}</SectionTitle>
        {walk?.truncated ? (
          <p className="text-12 text-warning" data-testid="copy-parts-cut">
            The list stops here: a walk looks at {WALK_LIMITS.entities} entities, {WALK_LIMITS.relations} relations and {WALK_LIMITS.depth} levels at most, so
            some parts further down are not shown.
          </p>
        ) : null}
        {parts.length === 0 ? (
          <p className="text-12 text-secondary" data-testid="copy-no-parts">
            {relations.isPending || !walk
              ? "Looking for parts…"
              : `${entity.name} owns no parts: no composition has it as the whole. The fields and links above still apply.`}
          </p>
        ) : (
          <div className="flex flex-col" role="tree" aria-label="Parts">
            {parts.map((p) => (
              <PartRow
                key={p.id}
                part={p}
                entityId={id}
                merged={partMerged}
                inherited={partBase}
                hasOwn={hasOwnCopyEntry(json as Json, "parts", p.end)}
                locked={locked}
                nameOf={nameOf}
                partChain={(partChains.get(p.entity) ?? [p.entity]).flatMap((c) => (partDocs.byId.get(c)?.json ? [partDocs.byId.get(c)!.json as Json] : []))}
                scalarBase={scalarBase}
                memberNames={memberNames}
                lifecycles={(governing.get(p.entity) ?? []).flatMap((l) =>
                  lifecycleDocs.byId.get(l)?.json ? [lifecycleDocs.byId.get(l)!.json as unknown as ProcessDoc] : [],
                )}
                commit={commit}
              />
            ))}
          </div>
        )}
      </section>

      {problems.length ? (
        <ul className="flex flex-col gap-0.5 text-12 text-danger" role="alert" data-testid="copy-findings">
          {problems.map((d, i) => (
            <li key={i}>
              {d.rule}: {d.message}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

/** A select of the words an entry may take; the word it has now stays listed when it no longer fits (the Problems panel says why). */
function WordSelect<W extends string>({
  label,
  value,
  words,
  labels,
  disabled,
  onChange,
}: {
  label: string;
  value: string;
  words: readonly W[];
  labels: Record<W, string>;
  disabled?: boolean;
  onChange: (word: W) => void;
}) {
  const all = words.includes(value as W) ? words : [...words, value as W];
  return (
    <Select aria-label={label} className="h-6" value={value} disabled={disabled} onChange={(e) => onChange(e.target.value as W)}>
      {all.map((w) => (
        <option key={w} value={w}>
          {labels[w] ?? w}
        </option>
      ))}
    </Select>
  );
}

function PartRow({
  part,
  entityId,
  merged,
  inherited,
  hasOwn,
  locked,
  nameOf,
  partChain,
  scalarBase,
  memberNames,
  lifecycles,
  commit,
}: {
  part: PartInfo;
  entityId: string;
  merged: ReturnType<typeof mergedEntries>;
  inherited: ReturnType<typeof mergedEntries>;
  /** The entity writes an entry of its own for the part. */
  hasOwn: boolean;
  locked: boolean;
  nameOf: (id: string | null | undefined) => string;
  partChain: Json[];
  scalarBase: (typeId: string) => unknown;
  memberNames: ReadonlyMap<string, string>;
  lifecycles: ProcessDoc[];
  commit: (update: (j: Json) => void) => void;
}) {
  const [open, setOpen] = useState(false);
  // One chosen part waits here until its choice is picked: `one` without a choice is refused (MQ3047), so it is not written before.
  const [pendingOne, setPendingOne] = useState(false);
  const entry = merged.get(part.end);
  const base = inherited.get(part.end)?.entry;
  const stored = String(entry?.entry.onCopy ?? "all") as PartWord;
  const word: PartWord = pendingOne && stored !== "one" ? "one" : stored;
  const from = entry && entry.from !== entityId ? entry.from : null;
  // A part is left out when one above it is.
  const segments = part.id.split("/");
  const left = segments.slice(0, -1).some((end) => merged.get(end)?.entry.onCopy === "none");
  const attributes = copyAttributes(partChain, scalarBase);
  const fieldEntries = new Map(arr(entry?.entry.attributes).map((a) => [String(a.attribute), String(a.onCopy)]));
  const label = `${part.role}`;
  return (
    <div
      className={cn("border-b border-default py-0.5", (from || left) && "text-secondary")}
      role="treeitem"
      aria-level={part.depth}
      data-testid="copy-part-row"
      data-name={part.role}
    >
      <div className="flex items-center gap-1" style={{ paddingLeft: `${(part.depth - 1) * 16}px` }}>
        {word !== "none" && !left ? (
          <button
            type="button"
            className="inline-flex size-4 items-center justify-center rounded-control hover:bg-hover"
            aria-label={`${open ? "Hide" : "Show"} the fields of ${label}`}
            aria-expanded={open}
            title={`${open ? "Hide" : "Show"} the fields of ${label}`}
            onClick={() => setOpen(!open)}
            data-testid="copy-part-fields-toggle"
          >
            {open ? <ChevronDown className="size-3" aria-hidden /> : <ChevronRight className="size-3" aria-hidden />}
          </button>
        ) : (
          <span className="size-4" aria-hidden />
        )}
        <span className="min-w-0 flex-1 truncate text-13">
          {label} <span className="text-12 text-secondary">{nameOf(part.entity)}</span>
          {part.recursive ? <span className="ml-1 text-11 text-secondary">(its own kind)</span> : null}
          {left ? <span className="ml-1 text-11">(left out with its owner)</span> : null}
        </span>
        <span className="w-28 shrink-0">
          <WordSelect
            label={`${label} when copied`}
            value={word}
            words={["all", "one", "none"] as const}
            labels={PART_WORDS}
            disabled={locked}
            onChange={(w) => {
              if (w === "one") {
                // With a choice already written (or a base's), `one` is written now; else it waits for the choice.
                if (stored !== "one" && (entry?.entry.choose || base?.choose)) commit((j) => setPartWord(j, part.end, "one", base));
                else if (stored !== "one") setPendingOne(true);
                return;
              }
              setPendingOne(false);
              commit((j) => setPartWord(j, part.end, w, base));
            }}
          />
        </span>
        <span className="w-48 shrink-0 text-12 text-secondary">
          {from ? `from ${nameOf(from)}` : ""}
          {!from && hasOwn && base ? (
            <Button size="sm" variant="ghost" disabled={locked} onClick={() => commit((j) => removeOwnPart(j, part.end))} data-testid="copy-back-to-base">
              Back to base
            </Button>
          ) : null}
        </span>
      </div>
      {word === "one" ? (
        <PartChoice
          part={part}
          choice={choiceOf(stored === "one" ? entry?.entry : undefined)}
          pending={pendingOne && stored !== "one"}
          locked={locked}
          partChain={partChain}
          scalarBase={scalarBase}
          memberNames={memberNames}
          lifecycles={lifecycles}
          onChange={(c) => {
            commit((j) => void setPartChoice(j, part.end, base, c));
            if (c.condition || c.state || c.latestBy) setPendingOne(false);
          }}
        />
      ) : null}
      {open && word !== "none" ? (
        <div className="flex flex-col gap-0.5 pb-1" style={{ paddingLeft: `${(part.depth - 1) * 16 + 20}px` }} data-testid="copy-part-fields">
          {attributes.length ? (
            attributes.map((a) => (
              <div key={a.id} className="flex items-center gap-2 text-13">
                <span className="w-44 truncate">{a.name}</span>
                <span className="w-44">
                  <WordSelect
                    label={`${a.name} of ${label} when copied`}
                    value={fieldEntries.get(a.id) ?? "copy"}
                    words={a.words}
                    labels={ATTRIBUTE_WORDS}
                    disabled={locked}
                    onChange={(w) => commit((j) => setPartAttributeWord(j, part.end, base, a.id, w))}
                  />
                </span>
              </div>
            ))
          ) : (
            <p className="text-12 text-secondary">{nameOf(part.entity)} has no attribute a copy could change.</p>
          )}
        </div>
      ) : null}
    </div>
  );
}

/** How one part is chosen under each copied owner: a condition on its attributes or a state of a lifecycle, and the attribute that says which is latest. */
function PartChoice({
  part,
  choice,
  pending,
  locked,
  partChain,
  scalarBase,
  memberNames,
  lifecycles,
  onChange,
}: {
  part: PartInfo;
  choice: { condition?: Json; state?: string; latestBy?: string };
  /** `one` is picked but nothing is written yet: no choice has been made. */
  pending: boolean;
  locked: boolean;
  partChain: Json[];
  scalarBase: (typeId: string) => unknown;
  memberNames: ReadonlyMap<string, string>;
  lifecycles: ProcessDoc[];
  onChange: (choice: Choice) => void;
}) {
  const initial = choice.condition ? "condition" : choice.state ? "state" : "latest";
  const [picked, setPicked] = useState<"condition" | "state" | "latest" | null>(null);
  // What is stored moved (an undo, another edit): follow it.
  useEffect(() => setPicked(null), [initial]);
  const mode = picked ?? initial;
  const groups = stateChoices(lifecycles);
  const partAttributes = [...partChain].reverse().flatMap((d) => arr(d.attributes) as unknown as AttributeLike[]);
  const ordered = partAttributes.filter((a) => !a.collection && isOrderable(a.type, scalarBase));
  const names = new Map<string, string>([...memberNames, ...partAttributes.map((a) => [a.id, a.name] as const)]);
  const conditionText = choice.condition ? printCondition(choice.condition, { name: (x) => names.get(x) }) : "";
  const dom = `copy-choice-${part.id.replace(/[^a-zA-Z0-9]/g, "")}`;
  const hint = pending
    ? "Pick how the one part is chosen."
    : mode === "latest" && !choice.latestBy
      ? "Pick Latest by: the part with the greatest value is copied."
      : null;
  return (
    <div
      className="flex flex-wrap items-start gap-2 pb-1 pt-0.5 text-12"
      style={{ paddingLeft: `${(part.depth - 1) * 16 + 20}px` }}
      data-testid="copy-part-choice"
    >
      <label className="flex items-center gap-1">
        Choose by
        <Select
          aria-label={`How ${part.role} is chosen`}
          className="h-6 w-28"
          value={mode}
          disabled={locked}
          onChange={(e) => {
            const next = e.target.value as "condition" | "state" | "latest";
            setPicked(next);
            // Choosing by the latest alone writes it when there is a latest-by attribute; the others wait for their value.
            if (next === "latest") onChange({ latestBy: choice.latestBy });
          }}
        >
          <option value="condition">a condition</option>
          <option value="state">a state</option>
          <option value="latest">nothing else</option>
        </Select>
      </label>
      {mode === "condition" ? (
        conditionText === null ? (
          <span className="pt-1 text-secondary" data-testid="copy-condition-tree">
            A condition the box can&apos;t show: edit it on the JSON tab.
          </span>
        ) : (
          <div className="w-64">
            <ValueBox
              key={conditionText}
              id={`${dom}-condition`}
              label={`Condition for ${part.role}`}
              initial={conditionText}
              placeholder="a condition on its fields, such as current = true"
              request={() => null}
              onChange={() => undefined}
              onCommit={(text) => {
                if (text.trim() && text.trim() !== conditionText) onChange({ condition: text, latestBy: choice.latestBy });
              }}
              condition
            />
          </div>
        )
      ) : null}
      {mode === "state" ? (
        <Select
          aria-label={`State for ${part.role}`}
          className="h-6 w-56"
          value={choice.state ?? ""}
          disabled={locked || !groups.length}
          title={groups.length ? undefined : "No lifecycle governs this part."}
          onChange={(e) => e.target.value && onChange({ state: e.target.value, latestBy: choice.latestBy })}
        >
          <option value="">{groups.length ? "Pick a state" : "No lifecycle governs it"}</option>
          {groups.map((g) => (
            <optgroup key={g.lifecycle} label={g.lifecycle}>
              {g.states.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.label.slice(g.lifecycle.length + 1)}
                </option>
              ))}
            </optgroup>
          ))}
        </Select>
      ) : null}
      <label className="flex items-center gap-1">
        Latest by
        <Select
          aria-label={`Latest ${part.role} by`}
          className="h-6 w-40"
          value={choice.latestBy ?? ""}
          disabled={locked}
          onChange={(e) => onChange({ condition: choice.condition, state: choice.state, latestBy: e.target.value || undefined })}
        >
          <option value="">Not set</option>
          {ordered.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name}
            </option>
          ))}
        </Select>
      </label>
      {hint ? (
        <span className="pt-1 text-secondary" data-testid="copy-choice-hint">
          {hint}
        </span>
      ) : null}
    </div>
  );
}
