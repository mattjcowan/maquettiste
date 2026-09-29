// Localization server state (reference-types-seeds-localization.md 3.9): the settings and completeness, one owner's
// entries per locale, the translation queue's pages, and the write with its shard hashes (409: reread, tell the user).
import { useEffect, useMemo } from "react";
import { useQueries, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import type { TranslationEntry, TranslationField } from "@/api/types";
import { effectiveContentLocale, localizationEnabled, translatedLocales } from "./model";
import { setEffectiveContentLocale, useContentLocale } from "./contentLocale";

export const l10nKeys = {
  status: ["localization"] as const,
  all: ["translations"] as const,
  owner: (locale: string, owner: string) => ["translations", locale, "owner", owner] as const,
  shard: (locale: string, shard: string) => ["translations", locale, "shard", shard] as const,
};

/** Settings and completeness; a server without the operation answers as a project without localization. */
export function useLocalization() {
  const query = useQuery({
    queryKey: l10nKeys.status,
    queryFn: () => endpoints.getLocalization().catch(() => ({ defaultLocale: null, declared: [], locales: [] })),
    staleTime: 30_000,
  });
  const enabled = localizationEnabled(query.data);
  const locales = useMemo(() => translatedLocales(query.data), [query.data]);
  return { ...query, enabled, locales };
}

/** Keeps the content locale in effect in step with the settings; a change refetches the index in that locale. */
export function LocalizationSync() {
  const qc = useQueryClient();
  const status = useLocalization();
  const { preferred } = useContentLocale();
  const next = status.isSuccess ? effectiveContentLocale(preferred, status.data) : null;
  useEffect(() => {
    if (setEffectiveContentLocale(next)) void qc.invalidateQueries({ queryKey: keys.index, exact: true });
  }, [next, qc]);
  return null;
}

/** One owner's entries in every translated locale (the Translations section). */
export function useOwnerTranslations(owner: string, locales: readonly string[]) {
  return useQueries({
    queries: locales.map((locale) => ({
      queryKey: l10nKeys.owner(locale, owner),
      queryFn: () => endpoints.getTranslations(locale, { owner }),
      staleTime: 5_000,
    })),
  });
}

/** Every entry of a shard in a locale (the translation queue). */
export function useShardTranslations(locale: string | null, shard: string | null) {
  return useQuery({
    queryKey: l10nKeys.shard(locale ?? "", shard ?? ""),
    queryFn: () => endpoints.getTranslations(locale!, { shard: shard! }),
    enabled: !!locale && !!shard,
    staleTime: 5_000,
  });
}

export interface TranslationEdit {
  id: string;
  field: TranslationField;
  /** The text; null or "" removes the translation. */
  value: string | null;
  /** Confirm a stale translation: its source fingerprint is rewritten, the text is kept. */
  confirm?: boolean;
}

/**
 * Writes translations of one locale against the hashes of the shards they were read from. A changed shard (409) or an
 * invalid write (422) returns its message; either way the entries and completeness are refetched.
 */
export async function writeTranslations(
  qc: QueryClient,
  locale: string,
  edits: readonly TranslationEdit[],
  read: readonly Pick<TranslationEntry, "id" | "field" | "shard" | "shardHash">[],
): Promise<{ ok: true } | { ok: false; message: string }> {
  const expected: Record<string, string> = {};
  for (const e of edits) {
    const entry = read.find((r) => r.id === e.id && r.field === e.field);
    if (entry) expected[entry.shard] = entry.shardHash ?? "";
  }
  const result = await endpoints.saveTranslations(locale, {
    entries: edits.map((e) => ({ id: e.id, field: e.field, value: e.value === "" ? null : e.value, ...(e.confirm ? { confirm: true } : {}) })),
    expected,
  });
  void qc.invalidateQueries({ queryKey: [...l10nKeys.all, locale] });
  void qc.invalidateQueries({ queryKey: l10nKeys.status });
  if (result.status === 409) return { ok: false, message: "The translations changed on disk since they were read; they were reloaded. Try again." };
  if (result.status >= 400) return { ok: false, message: result.diagnostics.map((d) => `${d.rule} ${d.message}`).join(" ") || `HTTP ${result.status}` };
  return { ok: true };
}
