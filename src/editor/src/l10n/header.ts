// Header editing in a content locale (reference-types-seeds-localization.md 3.10, step 8): while a non-default content
// locale is in effect, Display name, Plural name and Description edit that locale's translation, with the default
// text as the placeholder. Pure, so it is tested alone.
import type { TranslationEntry } from "@/api/types";

export type HeaderField = "displayName" | "pluralName" | "description";

/** The entry a header field edits in the content locale, or null when it edits the default text. */
export function headerEntry(locale: string | null, entries: readonly TranslationEntry[], id: string, field: HeaderField): TranslationEntry | null {
  if (!locale) return null;
  return entries.find((e) => e.id === id && e.field === field) ?? null;
}

/** The edit a changed value writes: null clears the translation (the default shows again). */
export function headerEdit(entry: TranslationEntry, value: string): { id: string; field: TranslationEntry["field"]; value: string | null } | null {
  const next = value.trim() === "" ? null : value;
  if ((entry.translation ?? null) === next) return null;
  return { id: entry.id, field: entry.field, value: next };
}
