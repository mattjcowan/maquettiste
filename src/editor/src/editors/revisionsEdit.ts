// The conversions behind the entity editor's Revisions tab (revisions-design.md section 12): a level as a row of fields and back,
// the edits that write the entity's own block, the attributes the "Held in" picker may offer (decision 11) and the preview's runs.
// Pure, so the tab's behaviour is tested without a screen.
import {
  firstRevision,
  levelExamples,
  nextRevision,
  type MergedRevisions,
  type RevisionSequence,
  type SequenceLevel,
} from "@/model/revisionSequence";

export const MAX_LEVELS = 3;

export type LevelKind = "letters" | "numbers" | "list";

/** One level as the fields of a table row. */
export interface LevelRow {
  name: string;
  kind: LevelKind;
  /** The first value: a letter run, or a whole number written out. */
  first: string;
  /** Skipped letters, as the person types them ("I, O, Q"). */
  skip: string;
  width: string;
  startEmpty: boolean;
  /** A list's values, separated by commas. */
  values: string;
}

export const LEVEL_KIND_LABELS: Record<LevelKind, string> = { letters: "Letters", numbers: "Numbers", list: "A list" };

export function levelToRow(level: SequenceLevel): LevelRow {
  const kind: LevelKind = Array.isArray(level.values) ? "list" : level.format === "numbers" ? "numbers" : "letters";
  return {
    name: level.name ?? "",
    kind,
    first: level.first === undefined ? "" : String(level.first),
    skip: (level.skip ?? []).join(", "),
    width: level.width === undefined ? "" : String(level.width),
    startEmpty: level.startEmpty === true,
    values: (level.values ?? []).join(", "),
  };
}

/** The skipped letters in a text: upper case letters, each once, in the order written. */
export function parseSkip(text: string): string[] {
  return [...new Set(text.toUpperCase().match(/[A-Z]/g) ?? [])];
}

/** The values of a list in a text: separated by commas, each trimmed, empty ones and repeats dropped. */
export function parseListValues(text: string): string[] {
  return [...new Set(text.split(",").map((v) => v.trim()).filter((v) => v !== ""))];
}

/** A row written as a level; fields that don't belong to its kind, and empty fields, are left out. */
export function rowToLevel(row: LevelRow): SequenceLevel {
  const level: SequenceLevel = {};
  if (row.name.trim()) level.name = row.name.trim();
  if (row.kind === "list") level.values = parseListValues(row.values);
  else {
    level.format = row.kind;
    const first = row.first.trim();
    if (row.kind === "numbers") {
      if (/^[0-9]+$/.test(first)) level.first = Number(first);
      const width = Number(row.width);
      if (Number.isInteger(width) && width >= 1 && width <= 9) level.width = width;
    } else {
      if (/^[A-Za-z]+$/.test(first)) level.first = first.toUpperCase();
      const skip = parseSkip(row.skip);
      if (skip.length) level.skip = skip;
    }
  }
  if (row.startEmpty) level.startEmpty = true;
  return level;
}

/** A new level for the Add button: letters when it is the first, else numbers that start empty (the usual "A, A.1, A.2"). */
export const newLevel = (existing: number): SequenceLevel =>
  existing === 0 ? { format: "letters" } : { format: "numbers", startEmpty: true };

/** The sequence a set of levels and a separator write, for the preview. */
export const sequenceOf = (levels: readonly SequenceLevel[], separator: string): RevisionSequence => ({ levels, separator });

// ---------------------------------------------------------------- edits to the entity's own block

type Rec = Record<string, unknown>;

/** The entity's own revisions block, created when there is none. */
function ownBlock(json: Rec): Rec {
  if (typeof json.revisions !== "object" || json.revisions === null) json.revisions = {};
  return json.revisions as Rec;
}

/** A block with no key is not valid (entity.json: at least one key): drop it. */
function pruneBlock(json: Rec): void {
  const block = json.revisions as Rec | undefined;
  if (block && Object.keys(block).length === 0) delete json.revisions;
}

/** Add revisions: a block with one letters level. */
export function addRevisions(json: Rec): void {
  json.revisions = { levels: [{ format: "letters" }] };
}

/**
 * Gives the entity its own levels, which replace a base's as a whole. An empty list removes them, so a base's apply again
 * (and a family with none shows MQ8301).
 */
export function setOwnLevels(json: Rec, levels: readonly SequenceLevel[]): void {
  const block = ownBlock(json);
  if (levels.length) block.levels = levels.slice(0, MAX_LEVELS);
  else delete block.levels;
  pruneBlock(json);
}

/** The separator written for the entity; undefined takes a base's (else "."). An empty text is a separator (the levels run together). */
export function setOwnSeparator(json: Rec, separator: string | undefined): void {
  const block = ownBlock(json);
  if (separator === undefined) delete block.separator;
  else block.separator = separator;
  pruneBlock(json);
}

/** The attribute that holds the revision; undefined takes a base's, else the team's system holds it. */
export function setOwnBoundAttribute(json: Rec, attribute: string | undefined): void {
  const block = ownBlock(json);
  if (attribute === undefined) delete block.boundAttribute;
  else block.boundAttribute = attribute;
  pruneBlock(json);
}

/** Moves the item at `index` by `by` places (-1 up, 1 down); the same array when it can't move. */
export function moved<T>(items: readonly T[], index: number, by: -1 | 1): T[] {
  const to = index + by;
  if (index < 0 || index >= items.length || to < 0 || to >= items.length) return [...items];
  const out = [...items];
  [out[index], out[to]] = [out[to], out[index]];
  return out;
}

// ---------------------------------------------------------------- what the tab shows

export interface LevelsView {
  rows: LevelRow[];
  /** The base whose levels these are, to show "from <Base>"; null when the entity's own (or none) apply. */
  inheritedFrom: string | null;
}

/** The rows the Levels table shows: the entity's own levels, else the nearest base's (decision 9). */
export function levelsView(merged: MergedRevisions | null, entityId: string): LevelsView {
  if (!merged) return { rows: [], inheritedFrom: null };
  return {
    rows: merged.levels.map(levelToRow),
    inheritedFrom: merged.levels.length && merged.levelsFrom !== entityId ? merged.levelsFrom : null,
  };
}

/** The choices of the Separator field: a few usual ones, plus the one in the file when it isn't among them. */
export function separatorChoices(current: string | undefined): { value: string; label: string }[] {
  const usual = [
    { value: ".", label: 'Dot "."' },
    { value: "-", label: 'Dash "-"' },
    { value: "/", label: 'Slash "/"' },
    { value: "_", label: 'Underscore "_"' },
    { value: "", label: "None (A1, B2)" },
  ];
  return current === undefined || usual.some((u) => u.value === current) ? usual : [...usual, { value: current, label: `"${current}"` }];
}

// ---------------------------------------------------------------- "Held in"

export interface HeldInCandidate {
  id: string;
  name: string;
  type?: unknown;
  collection?: boolean;
  derived?: unknown;
}

/**
 * The attributes the "Held in" picker offers (decision 11): single-valued, not derived, a string or text directly or through a
 * scalar type (`scalarBase` gives a custom type's base, a built-in keyword or a type id), and not the bound attribute of a lifecycle.
 */
export function heldInChoices(
  attributes: readonly HeldInCandidate[],
  scalarBase: (typeId: string) => unknown,
  lifecycleBound: ReadonlySet<string>,
): HeldInCandidate[] {
  const textual = (type: unknown): boolean => {
    for (let at = type, hops = 0; hops < 8; hops++) {
      if (at === "string" || at === "text") return true;
      if (typeof at === "object" && at !== null && typeof (at as { ref?: unknown }).ref === "string") {
        at = scalarBase((at as { ref: string }).ref);
        continue;
      }
      if (typeof at === "string") {
        // A custom type's base written as an id.
        const next = scalarBase(at);
        if (next === undefined) return false;
        at = next;
        continue;
      }
      return false;
    }
    return false;
  };
  return attributes.filter((a) => a.collection !== true && !a.derived && !lifecycleBound.has(a.id) && textual(a.type));
}

// ---------------------------------------------------------------- the preview

export interface PreviewRun {
  level: number;
  label: string;
  /** "A, B, C, D, E …": the values, then an ellipsis when more follow. */
  text: string;
}

export interface Preview {
  /** The first revision; "" when level 1 starts empty (no revision). */
  first: string;
  /** False when the levels are not well formed enough to give a first value (no levels). */
  ok: boolean;
  runs: PreviewRun[];
}

/** The first revision, then a short run per level: level 1 from the start, deeper levels moving on from the start. */
export function previewOf(seq: RevisionSequence): Preview {
  if (!seq.levels.length) return { first: "", ok: false, runs: [] };
  const first = firstRevision(seq);
  const runs: PreviewRun[] = seq.levels.map((level, i) => {
    const label = level.name ? `Level ${i + 1} (${level.name})` : `Level ${i + 1}`;
    if (i === 0 && Array.isArray(level.values)) {
      const all = levelExamples(level, 6);
      return { level: 1, label, text: all.length > 5 ? `${all.slice(0, 5).join(", ")} …` : all.join(", ") };
    }
    const want = i === 0 ? 5 : 3;
    const values: string[] = i === 0 && first !== "" ? [first] : [];
    let at = first;
    let more = true;
    while (values.length <= want) {
      const next = nextRevision(seq, at, i + 1);
      if (next === null) {
        more = false;
        break;
      }
      values.push(next);
      at = next;
    }
    const shown = values.slice(0, want);
    return { level: i + 1, label, text: shown.join(", ") + (more && values.length > want ? " …" : "") };
  });
  return { first, ok: true, runs };
}
