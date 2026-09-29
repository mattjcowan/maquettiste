// The editor's localization model (reference-types-seeds-localization.md 3.2, 3.6 and 3.10), pure and tested alone:
// whether localization shows at all (two or more declared locales; with one, nothing appears), the content locale in
// effect, completeness per locale and the matrix of locales × shards, the translation queue's walk, the index label
// patching from the `translations` member of model.changed, and the Settings › Locales draft and its checks.
import type { ElementSummary, LocalizationSettings, LocalizationStatus, TranslationEntry } from "@/api/types";
import { applyIndexPatch } from "@/api/indexPatch";

/** Localization appears only when the project declares two or more locales (RT 3.10). */
export function localizationEnabled(status: LocalizationStatus | null | undefined): boolean {
  return !!status?.defaultLocale && (status.declared?.length ?? 0) >= 2;
}

/** The locales other than the default, in the settings' order. */
export function translatedLocales(status: LocalizationStatus | null | undefined): string[] {
  if (!localizationEnabled(status)) return [];
  return (status!.declared ?? []).filter((l) => l !== status!.defaultLocale);
}

/** The content locale in effect: the preferred one when it is declared and not the default, else null (the default). */
export function effectiveContentLocale(preferred: string | null, status: LocalizationStatus | null | undefined): string | null {
  if (!preferred || !localizationEnabled(status)) return null;
  return translatedLocales(status).includes(preferred) ? preferred : null;
}

export interface Counts {
  expected: number;
  translated: number;
  missing: number;
  stale: number;
}

/** Whole percent translated (stale counts as not done); an empty set is complete. Rounded down, so 99.6 % is not 100 %. */
export function percent(c: Pick<Counts, "expected" | "translated">): number {
  return c.expected === 0 ? 100 : Math.floor((c.translated * 100) / c.expected);
}

/** One locale's totals over its shards. */
export function completenessOf(status: LocalizationStatus | null | undefined, locale: string): Counts {
  const total: Counts = { expected: 0, translated: 0, missing: 0, stale: 0 };
  for (const s of status?.locales.find((l) => l.locale === locale)?.shards ?? []) {
    total.expected += s.expected;
    total.translated += s.translated;
    total.missing += s.missing;
    total.stale += s.stale;
  }
  return total;
}

/** A shard path's stem: `.maquettiste/model/locales/fr/billing/catalog.json` gives `billing/catalog`. */
export function shardStem(path: string, locale: string): string {
  const marker = `/locales/${locale}/`;
  const at = path.indexOf(marker);
  const rest = at >= 0 ? path.slice(at + marker.length) : path;
  return rest.endsWith(".json") ? rest.slice(0, -5) : rest;
}

/** A shard's row label: "Reference data", "Not in a domain", or the domain path ("billing › catalog"). */
export function shardLabel(stem: string): string {
  if (stem === "_reference-data") return "Reference data";
  if (stem === "_root") return "Not in a domain";
  return stem.split("/").join(" › ");
}

export interface MatrixCell extends Counts {
  shard: string;
  percent: number;
}

export interface MatrixRow {
  stem: string;
  label: string;
  cells: Record<string, MatrixCell | undefined>;
}

/** The completeness matrix: one row per shard (domains A to Z, then Not in a domain, then Reference data), one column per
 * translated locale. A cell a locale has no shard for is undefined. */
export function completenessMatrix(status: LocalizationStatus | null | undefined): { locales: string[]; rows: MatrixRow[] } {
  const locales = translatedLocales(status);
  const rows = new Map<string, MatrixRow>();
  for (const l of status?.locales ?? []) {
    if (!locales.includes(l.locale)) continue;
    for (const s of l.shards) {
      const stem = shardStem(s.shard, l.locale);
      const row = rows.get(stem) ?? { stem, label: shardLabel(stem), cells: {} };
      rows.set(stem, row);
      row.cells[l.locale] = { ...s, percent: percent(s) };
    }
  }
  const rank = (stem: string) => (stem === "_reference-data" ? 2 : stem === "_root" ? 1 : 0);
  return { locales, rows: [...rows.values()].sort((a, b) => rank(a.stem) - rank(b.stem) || (a.stem < b.stem ? -1 : a.stem > b.stem ? 1 : 0)) };
}

/** Whether an entry still needs work in the queue: missing, only a fallback, or stale. */
export const needsWork = (e: Pick<TranslationEntry, "state">) => e.state !== "translated";

/** The next entry after `from` (or before, with `step` -1) that needs work; -1 when there is none. */
export function nextOpen(entries: readonly Pick<TranslationEntry, "state">[], from: number, step: 1 | -1 = 1): number {
  for (let i = from + step; i >= 0 && i < entries.length; i += step) if (needsWork(entries[i])) return i;
  return -1;
}

/** An entry's key in the queue and in writes. */
export const entryKey = (e: Pick<TranslationEntry, "id" | "field">) => `${e.id}/${e.field}`;

/**
 * The index rows with the content locale's display names from model.changed's `translations` (RT 3.8): only rows whose
 * name changed are replaced, in place, so the explorer applies a patch rather than rebuilding. The same array when the
 * event has nothing for the locale.
 */
export function applyTranslations(
  rows: readonly ElementSummary[],
  translations: readonly { locale: string; displayNames: Record<string, string> }[] | null | undefined,
  locale: string | null,
): readonly ElementSummary[] {
  if (!locale) return rows;
  const names = translations?.find((t) => t.locale === locale)?.displayNames;
  if (!names) return rows;
  const upserts: ElementSummary[] = [];
  for (const row of rows) {
    const name = names[row.id];
    if (name !== undefined && name !== (row.displayName ?? row.name)) upserts.push({ ...row, displayName: name });
  }
  return upserts.length ? applyIndexPatch(rows, upserts, []) : rows;
}

/** A locale's name for people ("French (fr)"), from the browser's language names; the tag alone when it has none. */
export function localeName(tag: string): string {
  try {
    const name = new Intl.DisplayNames(["en"], { type: "language" }).of(tag);
    return name && name !== tag ? `${name} (${tag})` : tag;
  } catch {
    return tag;
  }
}

// ---------------------------------------------------------------- Settings › Locales

/** The node kinds `require` may name (RT 3.2): element kinds and the sub-element kinds. */
export const REQUIRE_KINDS = [
  "package",
  "entity",
  "value-object",
  "scalar-type",
  "enum",
  "relation",
  "reference-type",
  "seed",
  "category",
  "attribute",
  "end",
  "enum-member",
  "reference-field",
  "reference-row",
] as const;

const BCP47 = /^[A-Za-z]{2,3}(?:-[A-Za-z]{4})?(?:-(?:[A-Za-z]{2}|\d{3}))?(?:-(?:[A-Za-z\d]{5,8}|\d[A-Za-z\d]{3}))*$/;

export const isLocaleTag = (tag: string) => BCP47.test(tag);

/** The settings' problems as MQ7201 would name them, so Save stays off until they are fixed. */
export function localizationProblems(l: LocalizationSettings): string[] {
  const out: string[] = [];
  const locales = l.locales ?? [l.defaultLocale];
  for (const tag of locales) if (!isLocaleTag(tag)) out.push(`'${tag}' is not a BCP 47 language tag.`);
  if (new Set(locales).size !== locales.length) out.push("A locale is listed twice.");
  if (!locales.includes(l.defaultLocale)) out.push(`The default locale '${l.defaultLocale}' is not among the supported locales.`);
  for (const [from, chain] of Object.entries(l.fallbacks ?? {})) {
    if (!locales.includes(from)) out.push(`Fallbacks are declared for '${from}', which is not a supported locale.`);
    for (const to of chain) if (!locales.includes(to)) out.push(`'${from}' falls back to '${to}', which is not a supported locale.`);
  }
  // A cycle: following first fallbacks from a locale comes back to it.
  for (const start of Object.keys(l.fallbacks ?? {})) {
    const seen = new Set<string>([start]);
    const walk = (at: string): boolean => (l.fallbacks?.[at] ?? []).some((next) => next === start || (!seen.has(next) && (seen.add(next), walk(next))));
    if (walk(start)) {
      out.push(`The fallbacks of '${start}' come back to it.`);
      break;
    }
  }
  return out;
}

/** Settings with a locale added (it is appended; the default stays first). */
export function addLocale(l: LocalizationSettings, tag: string): LocalizationSettings {
  const locales = l.locales ?? [l.defaultLocale];
  return locales.includes(tag) ? l : { ...l, locales: [...locales, tag] };
}

/** Settings with a locale removed, with its fallbacks and the fallbacks naming it. The default cannot be removed. */
export function removeLocale(l: LocalizationSettings, tag: string): LocalizationSettings {
  if (tag === l.defaultLocale) return l;
  const fallbacks: Record<string, string[]> = {};
  for (const [from, chain] of Object.entries(l.fallbacks ?? {})) {
    if (from === tag) continue;
    const kept = chain.filter((c) => c !== tag);
    if (kept.length) fallbacks[from] = kept;
  }
  const next: LocalizationSettings = { ...l, locales: (l.locales ?? [l.defaultLocale]).filter((x) => x !== tag) };
  if (Object.keys(fallbacks).length) next.fallbacks = fallbacks;
  else delete next.fallbacks;
  return next;
}

/** Settings with a locale's fallbacks set from a comma-separated list (empty: the default chain). */
export function setFallbacks(l: LocalizationSettings, tag: string, text: string): LocalizationSettings {
  const chain = text
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  const fallbacks = { ...(l.fallbacks ?? {}) };
  if (chain.length) fallbacks[tag] = chain;
  else delete fallbacks[tag];
  const next: LocalizationSettings = { ...l, fallbacks };
  if (!Object.keys(fallbacks).length) delete next.fallbacks;
  return next;
}

/** Settings with a completeness kind toggled in `require` (none listed: every node counts). */
export function toggleRequire(l: LocalizationSettings, kind: string, on: boolean): LocalizationSettings {
  const current = new Set(l.require ?? []);
  if (on) current.add(kind);
  else current.delete(kind);
  const require = REQUIRE_KINDS.filter((k) => current.has(k));
  const next: LocalizationSettings = { ...l, require };
  if (!require.length) delete next.require;
  return next;
}
