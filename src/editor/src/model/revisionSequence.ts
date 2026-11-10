// The written form of a revision (revisions-design.md section 6) and the merged `revisions` block of an entity (section 8). The
// engine's RevisionSequence holds the same functions in C#; the shared fixture tests/fixtures/revisions/sequence-cases.json keeps
// the two equal. Nothing here throws: a sequence that is not well formed (MQ8301) gives no values rather than an error.
import type { ElementSummary } from "@/api/types";

/** One level, as an entity's revisions block writes it (entity.json). */
export interface SequenceLevel {
  name?: string;
  format?: "letters" | "numbers";
  values?: string[];
  first?: string | number;
  skip?: string[];
  width?: number;
  startEmpty?: boolean;
}

export interface RevisionSequence {
  levels: readonly SequenceLevel[];
  /** Absent: ".". */
  separator?: string;
}

export const DEFAULT_SEPARATOR = ".";
const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");

const separatorOf = (seq: RevisionSequence) => seq.separator ?? DEFAULT_SEPARATOR;
const isList = (level: SequenceLevel) => Array.isArray(level.values);

// ---------------------------------------------------------------- one level

const alphabetOf = (level: SequenceLevel) => {
  const skipped = new Set(level.skip ?? []);
  return ALPHABET.filter((c) => !skipped.has(c));
};

const compareLetters = (a: string, b: string) => (a.length !== b.length ? (a.length < b.length ? -1 : 1) : a < b ? -1 : a > b ? 1 : 0);

function firstNumber(level: SequenceLevel): bigint {
  try {
    return BigInt(level.first ?? 1);
  } catch {
    return 1n;
  }
}

const formatNumber = (n: bigint, width: number | undefined) => n.toString().padStart(width ?? 0, "0");

/** The first value of a level on its own: a list's first value, the first letter not skipped (or `first`), `first` or 1. */
export function levelFirst(level: SequenceLevel): string {
  if (isList(level)) return level.values?.[0] ?? "";
  if (level.format === "numbers") return formatNumber(firstNumber(level), level.width);
  return typeof level.first === "string" && level.first !== "" ? level.first : (alphabetOf(level)[0] ?? "A");
}

function levelIsValue(level: SequenceLevel, text: string): boolean {
  if (isList(level)) return (level.values ?? []).includes(text);
  if (level.format === "numbers") {
    if (!/^[0-9]+$/.test(text)) return false;
    const n = BigInt(text);
    return formatNumber(n, level.width) === text && n >= firstNumber(level);
  }
  if (!/^[A-Z]+$/.test(text)) return false;
  const skipped = level.skip ?? [];
  if (skipped.length && [...text].some((c) => skipped.includes(c))) return false;
  return compareLetters(text, levelFirst(level)) >= 0;
}

/** The value after `text` in its level; null at the end of a list. */
function levelNext(level: SequenceLevel, text: string): string | null {
  if (isList(level)) {
    const values = level.values ?? [];
    const at = values.indexOf(text);
    return at < 0 || at + 1 >= values.length ? null : values[at + 1];
  }
  if (level.format === "numbers") return formatNumber(BigInt(text) + 1n, level.width);
  const alphabet = alphabetOf(level);
  if (!alphabet.length) return null;
  const chars = [...text];
  for (let i = chars.length - 1; i >= 0; i--) {
    const at = alphabet.indexOf(chars[i]);
    if (at < alphabet.length - 1) {
      chars[i] = alphabet[at + 1];
      return chars.join("");
    }
    chars[i] = alphabet[0];
  }
  return alphabet[0] + chars.join("");
}

function levelCompare(level: SequenceLevel, a: string, b: string): number {
  if (isList(level)) {
    const values = level.values ?? [];
    return Math.sign(values.indexOf(a) - values.indexOf(b));
  }
  if (level.format === "numbers") {
    const x = BigInt(a);
    const y = BigInt(b);
    return x < y ? -1 : x > y ? 1 : 0;
  }
  return compareLetters(a, b);
}

/** A level's first five values on their own (all of a shorter list). */
export function levelExamples(level: SequenceLevel, count = 5): string[] {
  const out: string[] = [];
  let at: string | null = levelFirst(level);
  while (at !== null && at !== "" && out.length < count) {
    out.push(at);
    at = levelNext(level, at);
  }
  return out;
}

// ---------------------------------------------------------------- a sequence

/** How many levels are never empty: the ones above the first level that starts empty. */
const requiredLevels = (seq: RevisionSequence) => {
  const at = seq.levels.findIndex((l) => l.startEmpty === true);
  return at < 0 ? seq.levels.length : at;
};

/** The revision a new object takes: levels above the first `startEmpty` level at their first values; "" when level 1 starts empty. */
export function firstRevision(seq: RevisionSequence): string {
  return writeRevision(seq, seq.levels.slice(0, requiredLevels(seq)).map(levelFirst));
}

/** The filled levels' values joined by the separator. */
export function writeRevision(seq: RevisionSequence, parts: readonly string[]): string {
  return parts.join(separatorOf(seq));
}

/** A text read as its filled levels, or null when it is not a value of the sequence. */
export function readRevision(seq: RevisionSequence, text: string): string[] | null {
  const levels = seq.levels;
  if (!levels.length) return null;
  if (text === "") return levels[0].startEmpty === true ? [] : null;
  const separator = separatorOf(seq);
  const parts = separator === "" ? (text.match(/[A-Z]+|[a-z]+|[0-9]+|[^A-Za-z0-9]+/g) ?? []) : text.split(separator);
  if (parts.length > levels.length || parts.length < requiredLevels(seq)) return null;
  return parts.every((p, i) => levelIsValue(levels[i], p)) ? parts : null;
}

export const isRevisionValue = (seq: RevisionSequence, text: string): boolean => readRevision(seq, text) !== null;

/** -1 when `a` comes first, 0 when equal, 1 when `b` comes first; for values (two texts that are not values compare as text). */
export function compareRevisions(seq: RevisionSequence, a: string, b: string): -1 | 0 | 1 {
  const pa = readRevision(seq, a);
  const pb = readRevision(seq, b);
  if (!pa || !pb) return a < b ? -1 : a > b ? 1 : 0;
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    if (pa[i] === undefined || pb[i] === undefined) return pa[i] === pb[i] ? 0 : pa[i] === undefined ? -1 : 1;
    const c = levelCompare(seq.levels[i], pa[i], pb[i]);
    if (c !== 0) return c < 0 ? -1 : 1;
  }
  return 0;
}

/**
 * The revision after moving at `level` (1 to 3): that level takes its next value (its first when empty), empty levels above take
 * their first values, levels below go back to their start (empty with `startEmpty`). Null when it can't move: `from` is not a
 * value, the level does not exist, or a list is at its last value.
 */
export function nextRevision(seq: RevisionSequence, from: string, level: number): string | null {
  const levels = seq.levels;
  const parts = readRevision(seq, from);
  if (!parts || level < 1 || level > levels.length) return null;
  const out: string[] = [];
  for (let i = 0; i < levels.length; i++) {
    if (i < level - 1) out.push(parts[i] ?? levelFirst(levels[i]));
    else if (i === level - 1) {
      const next = parts[i] === undefined ? levelFirst(levels[i]) : levelNext(levels[i], parts[i]);
      if (next === null) return null;
      out.push(next);
    } else if (levels[i].startEmpty === true) break;
    else out.push(levelFirst(levels[i]));
  }
  return writeRevision(seq, out);
}

export type ChosenRevision = { ok: true } | { ok: false; why: "not-a-value" | "not-later" };

/** Whether a chosen name may be given when the item's latest revision is `latest`: a value the sequence forms, and later. */
export function checkChosenRevision(seq: RevisionSequence, latest: string, name: string): ChosenRevision {
  if (!isRevisionValue(seq, name)) return { ok: false, why: "not-a-value" };
  if (compareRevisions(seq, name, latest) <= 0) return { ok: false, why: "not-later" };
  return { ok: true };
}

// ---------------------------------------------------------------- the merged block (section 8)

/** `restart`, `keep` or the id of a state. */
export type ReviseLifecycleRule = string;

export interface MergedRevisions {
  levels: SequenceLevel[];
  /** The entity that gives the levels: the one asked about or its nearest base with levels; the entity itself when none does. */
  levelsFrom: string;
  separator: string;
  /** The entity that writes the separator; null when none does (the default). */
  separatorFrom: string | null;
  boundAttribute: string | null;
  boundAttributeFrom: string | null;
  /** Every lifecycle that governs the entity; `from` null means no entry names it (the fallback, restart). */
  lifecycles: { lifecycle: string; onRevise: ReviseLifecycleRule; from: string | null }[];
  attributes: { attribute: string; onRevise: "reset" | "clear"; from: string }[];
  links: { end: string; onRevise: "stay" | "move" | "duplicate"; from: string }[];
}

export interface RevisionsBlock {
  levels?: SequenceLevel[];
  separator?: string;
  boundAttribute?: string;
  lifecycles?: { lifecycle: string; onRevise: string }[];
  attributes?: { attribute: string; onRevise: string }[];
  links?: { end: string; onRevise: string }[];
}

export type EntityWithRevisions = { base?: string; revisions?: RevisionsBlock } | undefined;

/**
 * The lifecycles that govern an entity: those that name it, or a base of it (`chain`: the entity then its bases), as a subject or in
 * the types they cover. Reads the index only, as the scenario editor does. A lifecycle on a stereotype is not found here.
 */
export function governingLifecycles(rows: readonly ElementSummary[], chain: readonly string[]): string[] {
  return rows
    .filter((p) => p.kind === "process" && p.use === "lifecycle")
    .filter((p) => {
      const subjects = [...(p.subject ? [p.subject] : []), ...(p.subjects ?? []), ...(p.covers ?? [])];
      return chain.some((c) => subjects.includes(c));
    })
    .map((p) => p.id);
}

/**
 * The block of an entity merged through its bases (decision 9): the nearest `levels`, `separator` and `boundAttribute`; entries added,
 * a nearer entry replacing a base's for the same lifecycle, attribute or end; `copy` undoes a base's attribute entry; the fallback
 * `restart` filled in for every lifecycle in `governing`. Null when neither the entity nor a base has a block.
 */
export function resolveRevisions(
  entityId: string,
  byId: ReadonlyMap<string, ElementSummary>,
  docOf: (id: string) => EntityWithRevisions,
  governing: readonly string[] = [],
): MergedRevisions | null {
  const chain = [entityId];
  for (let next = docOf(entityId)?.base ?? byId.get(entityId)?.base; next && !chain.includes(next); next = docOf(next)?.base ?? byId.get(next)?.base)
    chain.push(next);
  const family = chain.flatMap((id) => {
    const block = docOf(id)?.revisions;
    return block ? [{ id, block }] : [];
  });
  if (!family.length) return null;
  const out: MergedRevisions = {
    levels: [],
    levelsFrom: entityId,
    separator: DEFAULT_SEPARATOR,
    separatorFrom: null,
    boundAttribute: null,
    boundAttributeFrom: null,
    lifecycles: [],
    attributes: [],
    links: [],
  };
  for (const { id, block } of family) {
    if (block.levels && !out.levels.length) {
      out.levels = block.levels;
      out.levelsFrom = id;
    }
    if (block.separator !== undefined && out.separatorFrom === null) {
      out.separator = block.separator;
      out.separatorFrom = id;
    }
    if (block.boundAttribute && out.boundAttributeFrom === null) {
      out.boundAttribute = block.boundAttribute;
      out.boundAttributeFrom = id;
    }
  }
  const lifecycles = new Map<string, { onRevise: string; from: string }>();
  const attributes = new Map<string, { onRevise: "reset" | "clear"; from: string } | null>();
  const links = new Map<string, { onRevise: "stay" | "move" | "duplicate"; from: string }>();
  for (const { id, block } of [...family].reverse()) {
    for (const e of block.lifecycles ?? []) lifecycles.set(e.lifecycle, { onRevise: e.onRevise, from: id });
    for (const e of block.attributes ?? [])
      attributes.set(e.attribute, e.onRevise === "copy" ? null : { onRevise: e.onRevise as "reset" | "clear", from: id });
    for (const e of block.links ?? []) links.set(e.end, { onRevise: e.onRevise as "stay" | "move" | "duplicate", from: id });
  }
  out.lifecycles = governing.map((lifecycle) => ({
    lifecycle,
    onRevise: lifecycles.get(lifecycle)?.onRevise ?? "restart",
    from: lifecycles.get(lifecycle)?.from ?? null,
  }));
  out.attributes = [...attributes].flatMap(([attribute, v]) => (v ? [{ attribute, ...v }] : []));
  out.links = [...links].map(([end, v]) => ({ end, ...v }));
  return out;
}
