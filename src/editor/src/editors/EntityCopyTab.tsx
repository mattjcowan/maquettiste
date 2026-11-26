// The Copy tab of the entity editor (parts-and-copy-design.md section 3): what a copy of a whole record carries over. Fields (each attribute),
// Links (each to-one end the entity holds) and Parts (the entity's compositions as a tree). The entity's own `copy` block is what is written;
// an entry a base wrote shows "from <Base>" and is replaced by choosing another word. The parts are worked out here from the relations (the
// editor has no resolved model of an unsaved entity), the way the engine's `entity.parts` lists them.
import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import { loadElement, useElements, useIndex, useReferencesOf } from "@/api/queries";
import type { ElementSummary, EntityDoc } from "@/api/types";
import { Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import { indexLookup } from "@/model/index";
import { stateChoices, treeText, type TreeNames } from "@/model/effects";
import type { ProcessDoc } from "@/model/process";
import type { EditorContext } from "./EditorFrame";
import { baseChain } from "./related";
import { referrersOf, referencesOf, WALK_LIMITS } from "./refWalk";
import { ValueBox } from "./ValueBox";
import {
  ATTRIBUTE_WORDS,
  choiceOf,
  copyAttributes,
  inheritedEntries,
  isOrderable,
  LINK_WORDS,
  linkEnds,
  mergedEntries,
  partTree,
  PART_WORDS,
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
  sig: string;
}

/** A cheap signature of some index hashes, so the walk runs again when one of its relations is saved. */
function hashSignature(hashes: readonly (string | undefined)[]): string {
  let h = 0;
  for (const x of hashes) for (let i = 0; i < (x ?? "").length; i++) h = (h * 31 + x!.charCodeAt(i)) | 0;
  return `${hashes.length}:${h}`;
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
  const chain: Json[] = [json as Json, ...bases.flatMap((b) => (baseDocs.byId.get(b)?.json ? [baseDocs.byId.get(b)!.json as Json] : []))];
  const nameOf = (at: string | null | undefined) => (at ? (lookup.byId.get(at)?.name ?? at) : "");

  // The relations that touch an entity (it or a base) are the ones that reference it: asked one entity at a time, level by level down the
  // compositions, never from the whole index. Each entity is one where-used call per base; the walk is cut at WALK_LIMITS.
  const qc = useQueryClient();
  const kindOf = (x: string) => lookup.byId.get(x)?.kind;
  const rootIds = [id, ...bases];
  const rootRefs = useReferencesOf(rootIds);
  const rootSig = rootIds.map((c) => (rootRefs.get(c) ?? []).map((r) => r.fromElementId).join(",")).join("|");
  const [tick, setTick] = useState(0);
  const relations = useQuery({
    queryKey: ["copy-relations", id, bases.join(","), rootSig, tick],
    queryFn: async (): Promise<RelationWalk> => {
      const docs = new Map<string, Json>();
      const relatedIds = new Map<string, string[]>();
      const rowsOf = (e: string) => (relatedIds.get(e) ?? []).flatMap((r) => (lookup.byId.get(r) ? [lookup.byId.get(r)!] : []));
      let truncated = false;
      for (let round = 0; round < WALK_LIMITS.depth + 1; round++) {
        const tree = partTree(id, rowsOf, docs, baseOf);
        let todo = [...new Set([id, ...tree.map((p) => p.entity)])].filter((e) => !relatedIds.has(e));
        if (!todo.length) break;
        if (relatedIds.size + todo.length > WALK_LIMITS.entities) {
          truncated = true;
          todo = todo.slice(0, Math.max(0, WALK_LIMITS.entities - relatedIds.size));
          if (!todo.length) break;
        }
        for (const e of todo) {
          const found: string[] = [];
          for (let c: string | undefined = e, hops = 0; c && hops < 32; c = baseOf(c), hops++)
            found.push(...referrersOf(await referencesOf(qc, c), kindOf, "relation"));
          relatedIds.set(e, [...new Set(found)]);
        }
        const wanted = [...new Set([...relatedIds.values()].flat())].filter((r) => !docs.has(r));
        for (const doc of await Promise.all(wanted.map((r) => loadElement(r)))) docs.set(String(doc.json.id), doc.json as Json);
        if (round === WALK_LIMITS.depth) truncated = true;
      }
      return { docs, relatedIds, truncated, sig: hashSignature([...new Set([...relatedIds.values()].flat())].map((r) => lookup.byId.get(r)?.hash)) };
    },
    enabled: !index.isPending && rootIds.every((c) => rootRefs.has(c)),
  });
  const walk = relations.data;
  // A relation saved since the walk (its index row's hash moved) walks again.
  const nowSig = walk ? hashSignature([...new Set([...walk.relatedIds.values()].flat())].map((r) => lookup.byId.get(r)?.hash)) : "";
  useEffect(() => {
    if (walk && nowSig !== walk.sig) setTick((t) => t + 1);
  }, [walk, nowSig]);
  const relationDocs = walk?.docs ?? new Map<string, Json>();
  const related = (at: string): ElementSummary[] => (walk?.relatedIds.get(at) ?? []).flatMap((r) => (lookup.byId.get(r) ? [lookup.byId.get(r)!] : []));
  const parts = useMemo(() => partTree(id, related, relationDocs, baseOf), [walk, index.data, id]); // eslint-disable-line react-hooks/exhaustive-deps

  // The documents of every part entity and its bases (attributes, order), the lifecycles that name them (their referrers), and the scalar
  // types its attributes use that give an attribute its order: only what the parts' own attributes name.
  const partChains = useMemo(
    () => new Map(parts.map((p) => [p.entity, [p.entity, ...baseChain(lookup.byId, lookup.byId.get(p.entity)?.base)]] as const)),
    [parts, lookup],
  );
  const partDocIds = useMemo(() => [...new Set([...partChains.values()].flat())].sort(), [partChains]);
  const partDocs = useElements(partDocIds);
  const governingQuery = useQuery({
    queryKey: ["copy-governing", partDocIds.join(","), rootSig],
    queryFn: async () => {
      const out = new Map<string, string[]>();
      for (const [e, c] of partChains) {
        const found: string[] = [];
        for (const x of c) found.push(...referrersOf(await referencesOf(qc, x), kindOf, "process").filter((p) => lookup.byId.get(p)?.use === "lifecycle"));
        out.set(e, [...new Set(found)]);
      }
      return out;
    },
    enabled: partDocIds.length > 0,
  });
  const governing = useMemo(() => governingQuery.data ?? new Map<string, string[]>(), [governingQuery.data]);
  const lifecycleDocs = useElements(useMemo(() => [...new Set([...governing.values()].flat())].sort(), [governing]));
  const typeIds = useMemo(() => typeRefsOf([...partDocs.byId.values()].map((d) => d.json as Json)), [partDocs.byId]);
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

  const commit = (update: (j: Json) => void) => {
    ctx.edit((j) => void update(j as Json));
    ctx.flush();
  };
  const problems = ctx.diagnostics.filter((d) => /^\/copy(\/|$)/.test(d.jsonPointer ?? ""));

  // Fields
  const attributes = copyAttributes(chain);
  const attributeEntries = mergedEntries(chain, "attributes", "attribute");
  const attributeBase = inheritedEntries(chain, "attributes", "attribute");
  // Links
  const ends = linkEnds(id, related(id), relationDocs, baseOf);
  const linkMerged = mergedEntries(chain, "links", "end");
  const linkBase = inheritedEntries(chain, "links", "end");
  // Parts
  const partMerged = mergedEntries(chain, "parts", "end");
  const partBase = inheritedEntries(chain, "parts", "end");

  return (
    <div className="flex max-w-3xl flex-col gap-3" data-testid="editor-copy">
      <p className="text-12 text-secondary">
        What a copy of {entity.name} carries over. Anything not listed is copied as it is; a new copy always gets a new key and starts every lifecycle at its
        initial state.
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
                        onChange={(w) => commit((j) => setCopyWord(j, "attributes", a.id, w, attributeBase.get(a.id)?.entry.onCopy as string | undefined))}
                      />
                    </td>
                    <td className="w-32 px-1 text-12 text-secondary">{from ? `from ${nameOf(from)}` : ""}</td>
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
                        onChange={(w) => commit((j) => setCopyWord(j, "links", e.id, w, linkBase.get(e.id)?.entry.onCopy as string | undefined))}
                      />
                    </td>
                    <td className="w-32 px-1 text-12 text-secondary">{from ? `from ${nameOf(from)}` : ""}</td>
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
            The list stops here: a walk looks at {WALK_LIMITS.entities} entities and {WALK_LIMITS.depth} levels at most, so some parts further down are not
            shown.
          </p>
        ) : null}
        {parts.length === 0 ? (
          <p className="text-12 text-secondary" data-testid="copy-no-parts">
            {relations.isPending
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
                nameOf={nameOf}
                partChain={(partChains.get(p.entity) ?? [p.entity]).flatMap((c) => (partDocs.byId.get(c)?.json ? [partDocs.byId.get(c)!.json as Json] : []))}
                scalarBase={scalarBase}
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
  onChange,
}: {
  label: string;
  value: string;
  words: readonly W[];
  labels: Record<W, string>;
  onChange: (word: W) => void;
}) {
  const all = words.includes(value as W) ? words : [...words, value as W];
  return (
    <Select aria-label={label} className="h-6" value={value} onChange={(e) => onChange(e.target.value as W)}>
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
  nameOf,
  partChain,
  scalarBase,
  lifecycles,
  commit,
}: {
  part: PartInfo;
  entityId: string;
  merged: ReturnType<typeof mergedEntries>;
  inherited: ReturnType<typeof mergedEntries>;
  nameOf: (id: string | null | undefined) => string;
  partChain: Json[];
  scalarBase: (typeId: string) => unknown;
  lifecycles: ProcessDoc[];
  commit: (update: (j: Json) => void) => void;
}) {
  const [open, setOpen] = useState(false);
  const entry = merged.get(part.end);
  const base = inherited.get(part.end)?.entry;
  const word = String(entry?.entry.onCopy ?? "all") as PartWord;
  const from = entry && entry.from !== entityId ? entry.from : null;
  // A part is left out when one above it is.
  const segments = part.id.split("/");
  const left = segments.slice(0, -1).some((end) => merged.get(end)?.entry.onCopy === "none");
  const attributes = copyAttributes(partChain);
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
            onChange={(w) => commit((j) => setPartWord(j, part.end, w, base))}
          />
        </span>
        <span className="w-32 shrink-0 text-12 text-secondary">{from ? `from ${nameOf(from)}` : ""}</span>
      </div>
      {word === "one" ? (
        <PartChoice
          part={part}
          choice={choiceOf(entry?.entry)}
          partChain={partChain}
          attributes={attributes}
          scalarBase={scalarBase}
          lifecycles={lifecycles}
          onChange={(c) => commit((j) => setPartChoice(j, part.end, base, c))}
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
  partChain,
  attributes,
  scalarBase,
  lifecycles,
  onChange,
}: {
  part: PartInfo;
  choice: { condition?: Json; state?: string; latestBy?: string };
  partChain: Json[];
  attributes: readonly (AttributeLike & { owner: string })[];
  scalarBase: (typeId: string) => unknown;
  lifecycles: ProcessDoc[];
  onChange: (choice: Choice) => void;
}) {
  const initial = choice.condition ? "condition" : choice.state ? "state" : "latest";
  const [picked, setPicked] = useState<"condition" | "state" | "latest" | null>(null);
  const mode = picked ?? initial;
  const groups = stateChoices(lifecycles);
  const ordered = [...partChain]
    .reverse()
    .flatMap((d) => arr(d.attributes) as unknown as AttributeLike[])
    .filter((a) => !a.collection && isOrderable(a.type, scalarBase));
  const names = new Map(attributes.map((a) => [a.id, a.name]));
  const states = new Map(groups.flatMap((g) => g.states.map((s) => [s.id, s.label] as const)));
  const treeNames: TreeNames = { name: (x) => names.get(x), state: (x) => states.get(x), subject: () => false };
  const conditionText = choice.condition ? treeText(choice.condition, treeNames) : "";
  const dom = `copy-choice-${part.id.replace(/[^a-zA-Z0-9]/g, "")}`;
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
          onChange={(e) => {
            const next = e.target.value as "condition" | "state" | "latest";
            setPicked(next);
            // Switching the way drops the other's value, and keeps the latest-by attribute.
            if (next !== initial) onChange({ latestBy: choice.latestBy });
          }}
        >
          <option value="condition">a condition</option>
          <option value="state">a state</option>
          <option value="latest">nothing else</option>
        </Select>
      </label>
      {mode === "condition" ? (
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
              if (text.trim() !== conditionText) onChange({ condition: text, latestBy: choice.latestBy });
            }}
            condition
          />
        </div>
      ) : null}
      {mode === "state" ? (
        <Select
          aria-label={`State for ${part.role}`}
          className="h-6 w-56"
          value={choice.state ?? ""}
          disabled={!groups.length}
          title={groups.length ? undefined : "No lifecycle governs this part."}
          onChange={(e) => onChange({ state: e.target.value || undefined, latestBy: choice.latestBy })}
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
      {mode === "condition" || mode === "state" ? null : <span className="pt-1 text-secondary">The latest of all is copied.</span>}
    </div>
  );
}
