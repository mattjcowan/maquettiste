// Quick open's ranking (explorer-redesign.md 3.1), a pure function so the worker and the main thread (commands and
// screens) rank alike. Best first: exact, prefix, word-start across camel, snake and space boundaries (`invli` finds
// InvoiceLine, `inv_li` finds invoice_lines), substring, then a fuzzy subsequence. Ties go to the kind weight, then
// the domain of the current selection, then recently opened, then the shorter name, then alphabetical order.

export const Tier = { Exact: 0, Prefix: 1, WordStart: 2, Substring: 3, Fuzzy: 4, None: 9 } as const;
export type Tier = (typeof Tier)[keyof typeof Tier];

/** Kind weight: lower ranks first. Kinds not listed weigh the same, after the listed ones. */
export const KIND_WEIGHT: Readonly<Record<string, number>> = {
  entity: 0,
  "reference-type": 1,
  table: 2,
  relation: 3,
  enum: 4,
  "value-object": 5,
  "scalar-type": 6,
  diagram: 7,
};
const OTHER_WEIGHT = 8;
export const kindWeight = (kind: string) => KIND_WEIGHT[kind] ?? OTHER_WEIGHT;

/** The lower-cased words of a name, split at spaces, punctuation, underscores, camel humps and digit runs. */
export function wordsOf(name: string): string[] {
  return name
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .replace(/([A-Za-z])([0-9])/g, "$1 $2")
    .toLowerCase()
    .split(/[^a-z0-9À-￿]+/)
    .filter(Boolean);
}

/** Whether `q` (no separators) is a concatenation of non-empty prefixes of words, in order, words may be skipped. */
export function wordStartMatch(q: string, words: readonly string[]): boolean {
  if (!q) return false;
  const go = (qi: number, wi: number, depth: number): boolean => {
    if (qi === q.length) return true;
    if (depth > 64) return false;
    for (let w = wi; w < words.length; w++) {
      const word = words[w];
      let n = 0;
      while (n < word.length && qi + n < q.length && word[n] === q[qi + n]) n++;
      for (let take = n; take >= 1; take--) if (go(qi + take, w + 1, depth + 1)) return true;
    }
    return false;
  };
  return go(0, 0, 0);
}

/** Whether the characters of `q` appear in `s` in order. */
export function subsequence(q: string, s: string): boolean {
  let i = 0;
  for (let j = 0; j < s.length && i < q.length; j++) if (s[j] === q[i]) i++;
  return i === q.length;
}

/**
 * The tier function of one term, with the per-term work done once. `words` gives the candidate's words (the worker
 * caches them per row); they are asked for only when the cheaper tests leave a word-start match possible.
 */
export function tierFor(term: string): (lower: string, words: () => readonly string[]) => Tier {
  const compact = term.replace(/[\s_\-.]+/g, "");
  return (lower, words) => {
    if (!term) return Tier.None;
    if (lower === term) return Tier.Exact;
    if (lower.startsWith(term)) return Tier.Prefix;
    const sub = !!compact && subsequence(compact, lower);
    const inc = lower.includes(term);
    if (!sub && !inc) return Tier.None;
    if (sub && wordStartMatch(compact, words())) return Tier.WordStart;
    return inc ? Tier.Substring : Tier.Fuzzy;
  };
}

/** The tier of one candidate (`lower` is the name lower-cased; `name` keeps its case for camel humps). */
export function tierOf(term: string, name: string, lower = name.toLowerCase()): Tier {
  return tierFor(term)(lower, () => wordsOf(name));
}

/** The best tier over a candidate's names (name, display name, physical name). */
export function bestTier(term: string, names: readonly (string | null | undefined)[]): Tier {
  let best: Tier = Tier.None;
  for (const n of names) {
    if (!n) continue;
    const t = tierOf(term, n);
    if (t < best) best = t;
    if (best === Tier.Exact) break;
  }
  return best;
}

export interface Rankable {
  tier: Tier;
  kind: string;
  name: string;
  /** 0 when in the domain of the current selection, 1 when in an enclosing or enclosed domain, 2 otherwise. */
  near: number;
  /** Position in the recently opened list, or -1. */
  recent: number;
}

/**
 * The comparator's order as one number, for sorting many candidates fast: tier, kind weight, nearness, recency and
 * name length (names of equal key are then compared as strings).
 */
export function rankKey(r: Rankable): number {
  return r.tier * 1e9 + kindWeight(r.kind) * 1e8 + r.near * 1e7 + (r.recent < 0 ? 9999 : Math.min(r.recent, 9998)) * 1e3 + Math.min(r.name.length, 999);
}

/** The comparator for ranked results: tier, kind weight, near the selection, recently opened, shorter, alphabetical. */
export function compareRanked(a: Rankable, b: Rankable): number {
  return (
    a.tier - b.tier ||
    kindWeight(a.kind) - kindWeight(b.kind) ||
    a.near - b.near ||
    (a.recent < 0 ? 1e6 : a.recent) - (b.recent < 0 ? 1e6 : b.recent) ||
    a.name.length - b.name.length ||
    (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)
  );
}
