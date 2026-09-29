# Localization

Localization of the standard fields (docs/engineering/reference-types-seeds-localization.md section 3).

| File | Role |
| --- | --- |
| `LocaleChains.cs` | The `localization` settings: declared locales in unit order (default first, then ordinal), the fallback chain of a locale (the locale, its `fallbacks` or supported BCP 47 truncations, the default), the localizable node kinds and the MQ7201 checks |
| `LocaleShardReader.cs` | The `kind` probe the loader dispatches on (a shard outside `model/locales/<locale>/` loads with MQ1005) and the streamed read of a shard that passed schema validation before |
| `LocalizationIndex.cs` | `ModelSnapshot.Localization`, built lazily per snapshot: effective entry per (locale, id) (the ordinally first shard path wins, MQ7209), shards not loaded (MQ7202), the localizable nodes with their default-locale source texts and shard scope, per-field state (translated, missing, stale by the `src` fingerprint), completeness per (locale, shard), shard names (package kebab path; sibling collisions suffixed with the last 6 id characters, all but the ordinally first id; `_root`; `_reference-data`) and the `l:<locale>:<owner>` hashes |

Elsewhere: `Loading/ModelLoader.cs` (shards and their description sidecars, which resolve relative to `model/locales/<locale>/`),
`Loading/ChangePlanner.Translations.cs` (shard renames with packages, entry moves with elements, sidecar moves with element files,
entry removal on delete; an emptied shard is deleted), `ModelStore.Translations.cs` (read and write entries with shard ETags),
`Validation/LocalizationRules.cs` (MQ7201 to MQ7211), `Rendering/LocalizationHelpers.cs` (the six helpers and their dependency
keys), `Resolution/ResolvedCore.cs` (`RLocale`, `model.locales`, the `each locale` element).

Deviations and notes:
- The default-locale source of a plural name is the explicit `pluralName`, else the display name (the inflected plural is a
  resolution product the snapshot does not have); a sub-element's sidecar description is fingerprinted by its `file:` reference.
- Texts are kept in memory once parsed (plural names, labels and descriptions are not re-read on demand); the streamed reader avoids
  a document tree for shards that validated before.
- The tag vocabulary and category tree containers are not localizable nodes; their categories are (kind `category`).

## API, MCP and exchange files (RT step 5)

- `ModelStore.LocalizationApi.cs` holds the reads and writes both the functions (`LocalizationEndpoints`) and the MCP tools
  call: `GetLocalizationStatusAsync`, `GetTranslationRowsAsync` (owner, shard, or the paged `missing` queue: states missing,
  fallback and stale, 200 per page, cursor `id/field`), `ExportTranslationsAsync` / `ImportTranslationsAsync` (one
  `SaveTranslationsAsync` with the shard hashes read for the preview), `ExportSeedCsvAsync` / `ImportSeedCsvAsync`, and
  `GetReferenceTypeUsageAsync` (from the resolved model).
- `TranslationFiles.cs`: RFC 4180 `Csv` (LF, or BOM and CRLF with `bom`) and XLIFF 2.1 (one `file` per shard, units keyed
  `id/field`, `state` translated or initial, `subState` `maquettiste:<state>`; DTDs are refused).
- `ChangeSet.Locales` (not serialized) lists the locales whose shards or shard sidecars changed; it makes a translation save a
  non-empty change, and the host turns it into the `translations` of `model.changed`.
- `ModelReads.DisplayNames(snapshot, locale)` is the per-locale display-name table, built once per snapshot and locale and kept
  in memory beside it (not on the host volume); `LocalizedIndexTag` covers the locale, its chain and the chain's shard hashes.
- Deviations: a seed CSV import saves the seed and then its translations in two saves (the batch `translate` op is not wired
  yet); the translations event diffs against the snapshot last published and sends every display name of the locale the first
  time after a start.

## Cold load with locales (RT section 5)

`Maquettiste.Bench write-model --locales 4` and `time-load` measure it (bench/README.md). At the bench defaults (146,753
localizable nodes per locale), four complete locales add about 1.2 to 1.4 s to a cold load (shard load about 0.6 to 0.8 s, node
index 230 to 430 ms, completeness 280 to 390 ms), against the 400 ms target, which is not met. `Completeness` caches each
field's source fingerprint per snapshot (`SourceHashOf`) and counts the locales in parallel.
