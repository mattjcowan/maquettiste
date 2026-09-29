using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>The completeness of one shard (reference-types-seeds-localization.md section 3.6).</summary>
/// <param name="Shard">The shard's repo path.</param>
/// <param name="Expected">Fields expected.</param>
/// <param name="Translated">Fields translated and current.</param>
/// <param name="Missing">Fields without a translation.</param>
/// <param name="Stale">Fields whose default text changed since the translation.</param>
public sealed record ShardStatus(string Shard, int Expected, int Translated, int Missing, int Stale);

/// <summary>The completeness of one translated locale.</summary>
/// <param name="Locale">The locale.</param>
/// <param name="Chain">The fallback chain, the locale first and the default last.</param>
/// <param name="Shards">Per shard, ordinal by path.</param>
public sealed record LocaleStatus(string Locale, IReadOnlyList<string> Chain, IReadOnlyList<ShardStatus> Shards);

/// <summary>The body of <c>GET /api/localization</c>.</summary>
/// <param name="DefaultLocale">The default locale, or <see langword="null"/> when the project declares no <c>localization</c>.</param>
/// <param name="Declared">Every declared locale, the default included, as the settings list them.</param>
/// <param name="Locales">The locales other than the default, with their completeness.</param>
public sealed record LocalizationStatus(string? DefaultLocale, IReadOnlyList<string> Declared, IReadOnlyList<LocaleStatus> Locales);

/// <summary>One entry of <c>GET /api/localization/{locale}/entries</c>.</summary>
/// <param name="Id">The node id.</param>
/// <param name="Owner">The element whose file holds the node.</param>
/// <param name="Field">The field.</param>
/// <param name="Source">The default text.</param>
/// <param name="Translation">The locale's own text, or <see langword="null"/>.</param>
/// <param name="Effective">The text the fallback chain gives.</param>
/// <param name="State">translated, missing, stale or fallback.</param>
/// <param name="Shard">The repo path of the shard the entry belongs in.</param>
/// <param name="ShardHash">That shard's content hash (its ETag), or <see langword="null"/> while it does not exist.</param>
public sealed record TranslationRow(string Id, string Owner, string Field, string? Source, string? Translation, string? Effective, string State, string Shard, string? ShardHash);

/// <summary>A page of translation entries.</summary>
/// <param name="Entries">The entries, ordinal by id then field order.</param>
/// <param name="Cursor">The cursor of the next page, or <see langword="null"/> on the last one.</param>
public sealed record TranslationRows(IReadOnlyList<TranslationRow> Entries, string? Cursor);

/// <summary>What an import adds, changes and removes (reference-types-seeds-localization.md sections 2.3 and 3.9).</summary>
/// <param name="Added">Rows (or translations) the import adds.</param>
/// <param name="Changed">What changes, each with its before and after cells or texts.</param>
/// <param name="Removed">Rows a <c>replace</c> import removes.</param>
/// <param name="Diagnostics">Why units were ignored, or why applying was refused.</param>
public sealed record ImportPreview(int Added, IReadOnlyList<JsonObject> Changed, int Removed, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Whether the import was applied (false for a dry run and when it was refused).</summary>
    public bool Applied { get; init; }

    /// <summary>CSV headers that match no column, ignored.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? IgnoredHeaders { get; init; }

    /// <summary>Rows a <c>replace</c> import keeps because cells of other seeds name them, each with its referrers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonObject>? Blocked { get; init; }

    /// <summary>The seed's new hash when applied.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; init; }

    /// <summary>The new hashes of the shards written when applied.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ShardHashes { get; init; }
}

/// <summary>An import's preview with the outcome of applying it (<see cref="SaveOutcome.Saved"/> for a dry run).</summary>
/// <param name="Preview">The preview.</param>
/// <param name="Outcome">The outcome.</param>
public sealed record ImportResult(ImportPreview Preview, SaveOutcome Outcome);

/// <summary>One file of a several-seed CSV import.</summary>
/// <param name="Seed">The seed id.</param>
/// <param name="Content">The CSV text.</param>
/// <param name="Hash">The seed hash read for the preview, or <see langword="null"/> for the current one.</param>
public sealed record SeedCsvFile(string Seed, string Content, string? Hash);

/// <summary>The outcome of a several-seed CSV import: one preview per file, in request order.</summary>
/// <param name="Items">The previews (applied ones carry <c>applied</c> and the new hash).</param>
/// <param name="Outcome">Saved, or why nothing was written.</param>
public sealed record SeedBatchImportResult(IReadOnlyList<ImportPreview> Items, SaveOutcome Outcome);

/// <summary>One attribute typed by a reference type, as <c>GET /api/reference-types/{id}/usage</c> lists it.</summary>
/// <param name="Attribute">The attribute id.</param>
/// <param name="Owner">The element that declares it.</param>
/// <param name="Domain">The owner's package, or <see langword="null"/> at the root.</param>
/// <param name="Collection">Whether it holds several codes.</param>
/// <param name="Required">Whether it is required.</param>
/// <param name="Storage">Database name to the effective storage choice.</param>
public sealed record ReferenceTypeUse(string Attribute, string Owner, string? Domain, bool Collection, bool Required, IReadOnlyDictionary<string, StorageChoice> Storage);

/// <summary>The body of <c>GET /api/reference-types/{id}/usage</c>.</summary>
/// <param name="Usages">The usages, ordinal by owner then attribute id.</param>
public sealed record ReferenceTypeUsage(IReadOnlyList<ReferenceTypeUse> Usages);

/// <summary>The API and MCP reads and writes of reference-types-seeds-localization.md section 3.9.</summary>
public sealed partial class ModelStore
{
    /// <summary>The page size of <c>?missing=true</c>.</summary>
    public const int MissingPageSize = 200;

    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal) { "integer", "int", "long", "short", "byte", "decimal", "float", "double", "number", "money" };

    /// <summary>Whether <paramref name="locale"/> is a declared locale other than the default (one that has translations).</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it is.</returns>
    public async Task<bool> IsTranslatedLocaleAsync(string locale, CancellationToken ct) =>
        (await LoadedAsync(ct).ConfigureAwait(false)).Localization.IsTranslated(locale);

    /// <summary>The settings and the completeness per locale and shard.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    public async Task<LocalizationStatus> GetLocalizationStatusAsync(CancellationToken ct)
    {
        var l10n = (await LoadedAsync(ct).ConfigureAwait(false)).Localization;
        if (l10n.Settings is not { } settings)
            return new LocalizationStatus(null, [], []);
        var paths = _paths.Value;
        var locales = l10n.Locales.Where(l10n.IsTranslated).Select(locale => new LocaleStatus(locale, l10n.ChainOf(locale),
            [.. l10n.Completeness(locale).Select(c => new ShardStatus(paths.ToRepoPath(c.ShardPath), c.Expected, c.Translated, c.Missing, c.Stale))
                .OrderBy(s => s.Shard, StringComparer.Ordinal)])).ToList();
        return new LocalizationStatus(settings.DefaultLocale, settings.Locales, locales);
    }

    /// <summary>
    /// Entries of one locale by owner, by shard, or (with <paramref name="missing"/>) the paged entries that need work (missing, fallback
    /// or stale) for the translation queue.
    /// </summary>
    /// <param name="locale">A translated locale.</param>
    /// <param name="owner">An owner element id, or <see langword="null"/>.</param>
    /// <param name="shard">A shard repo path, or <see langword="null"/>.</param>
    /// <param name="missing">Only entries that need work, paged.</param>
    /// <param name="cursor">The cursor of the page (the previous page's last <c>id/field</c>).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entries.</returns>
    public async Task<TranslationRows> GetTranslationRowsAsync(string locale, string? owner, string? shard, bool missing, string? cursor, CancellationToken ct)
    {
        var page = await GetTranslationsAsync(locale, owner, shard, ct).ConfigureAwait(false);
        var rows = page.Items.Select(i => new TranslationRow(i.Id, i.OwnerId, i.Field, i.Source, i.Value, i.Effective, i.State, i.ShardPath,
            page.ShardHashes.TryGetValue(i.ShardPath, out var h) ? h : null));
        if (!missing)
            return new TranslationRows([.. rows], null);
        var pending = rows.Where(r => r.State != "translated");
        if (!string.IsNullOrEmpty(cursor))
            pending = pending.SkipWhile(r => r.Id + "/" + r.Field != cursor).Skip(1);
        var list = pending.Take(MissingPageSize + 1).ToList();
        if (list.Count <= MissingPageSize)
            return new TranslationRows(list, null);
        list.RemoveAt(MissingPageSize);
        return new TranslationRows(list, list[^1].Id + "/" + list[^1].Field);
    }

    /// <summary>Exports one locale (optionally one shard) as XLIFF 2.1 or CSV.</summary>
    /// <param name="locale">A translated locale.</param>
    /// <param name="format"><c>xliff</c> or <c>csv</c>.</param>
    /// <param name="shard">A shard repo path, or <see langword="null"/> for all.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The file text.</returns>
    public async Task<string> ExportTranslationsAsync(string locale, string format, string? shard, CancellationToken ct)
    {
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var page = await GetTranslationsAsync(locale, null, shard, ct).ConfigureAwait(false);
        return format == "csv"
            ? TranslationFiles.WriteCsv(page.Items)
            : TranslationFiles.WriteXliff(snapshot.Localization.Settings?.DefaultLocale ?? "und", locale, page.Items);
    }

    /// <summary>
    /// Previews, or applies as one save with the shards' hashes as read, an XLIFF or CSV translation import. A unit with an empty target is
    /// left alone; a unit naming no translatable field of the model is ignored with MQ7203.
    /// </summary>
    /// <param name="locale">A translated locale.</param>
    /// <param name="format"><c>xliff</c> or <c>csv</c>.</param>
    /// <param name="content">The file text.</param>
    /// <param name="dryRun">Only preview.</param>
    /// <param name="source">Who makes the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preview and the outcome.</returns>
    /// <exception cref="FormatException">The file cannot be read.</exception>
    public async Task<ImportResult> ImportTranslationsAsync(string locale, string format, string content, bool dryRun, ChangeSource source, CancellationToken ct)
    {
        var units = format == "csv" ? TranslationFiles.ReadCsv(content) : TranslationFiles.ReadXliff(content);
        var page = await GetTranslationsAsync(locale, null, null, ct).ConfigureAwait(false);
        var current = page.Items.ToDictionary(i => (i.Id, i.Field));
        var edits = new List<TranslationEdit>();
        var changed = new List<JsonObject>();
        var diagnostics = new List<Diagnostic>();
        var added = 0;
        foreach (var unit in units)
        {
            if (unit.Value.Length == 0)
                continue;
            if (!current.TryGetValue((unit.Id, unit.Field), out var item))
            {
                diagnostics.Add(RuleCatalog.Create("MQ7203", $"{unit.Id}/{unit.Field} is not a translatable field of the model; the unit is ignored.", unit.Id, null, null));
                continue;
            }

            if (item.Value == unit.Value)
                continue;
            if (item.Value is null)
                added++;
            else
                changed.Add(new JsonObject { ["id"] = unit.Id, ["field"] = unit.Field, ["before"] = item.Value, ["after"] = unit.Value });
            edits.Add(new TranslationEdit(unit.Id, unit.Field, unit.Value));
        }

        var preview = new ImportPreview(added, changed, 0, diagnostics);
        if (dryRun || edits.Count == 0)
            return new ImportResult(preview, SaveOutcome.Saved);
        var saved = await SaveTranslationsAsync(locale, edits, page.ShardHashes, source, ct).ConfigureAwait(false);
        return new ImportResult(preview with
        {
            Applied = saved.Outcome == SaveOutcome.Saved,
            ShardHashes = saved.ShardHashes,
            Diagnostics = [.. diagnostics, .. saved.Diagnostics],
        }, saved.Outcome);
    }

    /// <summary>
    /// A seed's rows as CSV (section 2.3): <c>@id</c>, then the seed's columns in order (<c>@code</c>, <c>@label</c>, <c>@description</c>
    /// for built-in ones, attribute names and end roles otherwise), then <c>@label:&lt;locale&gt;</c> and <c>@description:&lt;locale&gt;</c>
    /// per requested locale.
    /// </summary>
    /// <param name="seedId">The seed id.</param>
    /// <param name="bom">Adds a byte order mark and CRLF line ends.</param>
    /// <param name="locales">Translated locales whose label and description columns are added.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The CSV text, or <see langword="null"/> when no seed has the id.</returns>
    public async Task<string?> ExportSeedCsvAsync(string seedId, bool bom, IReadOnlyList<string> locales, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locales);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Element>(seedId) is not Seed seed)
            return null;
        var columns = SeedColumns(snapshot, seed);
        var l10n = snapshot.Localization;
        var wanted = locales.Where(l10n.IsTranslated).Distinct(StringComparer.Ordinal).ToList();
        IEnumerable<IReadOnlyList<string?>> Rows()
        {
            yield return ["@id", .. columns.Select(c => c.Header), .. wanted.SelectMany(l => new[] { "@label:" + l, "@description:" + l })];
            foreach (var row in seed.Rows)
            {
                yield return [row.Id, .. columns.Select((c, i) => CellText(i < row.Values.Count ? row.Values[i] : null, c)),
                    .. wanted.SelectMany(l => new[] { l10n.Text(l, row.Id, LocalizationIndex.LabelField), l10n.Text(l, row.Id, LocalizationIndex.DescriptionField) })];
            }
        }

        return Csv.Write(Rows(), bom);
    }

    /// <summary>
    /// Previews or applies a CSV import into a seed (section 2.3). Rows match by <c>@id</c>, else by <c>@code</c> for a reference type,
    /// else are new rows with new ids; <c>replace</c> also removes the rows the file leaves out, except rows other seeds' cells name.
    /// Applying saves the seed with <paramref name="expectedHash"/> (the seed's hash as read when null), then writes the
    /// <c>@label:&lt;locale&gt;</c> and <c>@description:&lt;locale&gt;</c> translations.
    /// </summary>
    /// <param name="seedId">The seed id.</param>
    /// <param name="csv">The CSV text.</param>
    /// <param name="replace">Replace (else merge).</param>
    /// <param name="dryRun">Only preview.</param>
    /// <param name="expectedHash">The seed hash the caller read, or <see langword="null"/>.</param>
    /// <param name="source">Who makes the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preview and the outcome, or <see langword="null"/> when no seed has the id.</returns>
    /// <exception cref="FormatException">The CSV is malformed or has no <c>@id</c> header.</exception>
    public async Task<ImportResult?> ImportSeedCsvAsync(string seedId, string csv, bool replace, bool dryRun, string? expectedHash, ChangeSource source, CancellationToken ct)
    {
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Element>(seedId) is not Seed seed || snapshot.GetDocument(seedId) is not { } document)
            return null;
        var plan = PlanSeedImport(snapshot, seed, document, csv, replace);
        var preview = plan.Preview;
        if (dryRun || (plan.Json is null && plan.Translations.Count == 0))
            return new ImportResult(preview, SaveOutcome.Saved);

        var hash = document.Hash;
        if (plan.Json is { } json)
        {
            var saved = await SaveAsync(seedId, Encoding.UTF8.GetBytes(json.ToJsonString()), expectedHash ?? document.Hash, source, ct).ConfigureAwait(false);
            if (saved.Outcome != SaveOutcome.Saved)
                return new ImportResult(preview with { Diagnostics = saved.Diagnostics }, saved.Outcome);
            hash = saved.Hash;
        }

        return await WriteImportTranslationsAsync(preview, hash, seedId, plan.Translations, source, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Previews or applies CSV imports into several seeds at once (the editor's Import seed data…). Each file is read as
    /// <see cref="ImportSeedCsvAsync"/> reads one; applying saves every changed seed in one all-or-nothing change (a stale hash or a
    /// new error in any seed writes none of them), then writes the translations the files carry.
    /// </summary>
    /// <param name="files">The seeds and their CSV text, each with the hash read for its preview (or <see langword="null"/>).</param>
    /// <param name="replace">Replace (else merge).</param>
    /// <param name="dryRun">Only preview.</param>
    /// <param name="source">Who makes the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One preview per file in request order and the outcome, or <see langword="null"/> when a file names no seed.</returns>
    /// <exception cref="FormatException">A CSV is malformed or has no <c>@id</c> header, or two files name one seed.</exception>
    public async Task<SeedBatchImportResult?> ImportSeedCsvBatchAsync(IReadOnlyList<SeedCsvFile> files, bool replace, bool dryRun, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var plans = new List<(SeedCsvFile File, ElementDocument Document, SeedImportPlan Plan)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (snapshot.Get<Element>(file.Seed) is not Seed seed || snapshot.GetDocument(file.Seed) is not { } document)
                return null;
            if (!seen.Add(seed.Id))
                throw new FormatException($"Two files import into seed '{seed.Name}'; send one file per seed.");
            plans.Add((file, document, PlanSeedImport(snapshot, seed, document, file.Content, replace)));
        }

        var previews = plans.Select(p => p.Plan.Preview).ToList();
        if (dryRun || plans.All(p => p.Plan.Json is null && p.Plan.Translations.Count == 0))
            return new SeedBatchImportResult(previews, SaveOutcome.Saved);

        var changes = new List<PlannedChange>();
        var changedAt = new List<int>();
        for (var i = 0; i < plans.Count; i++)
        {
            if (plans[i].Plan.Json is not { } json)
                continue;
            if (!TryParseRequest(Encoding.UTF8.GetBytes(json.ToJsonString()), plans[i].File.Seed, out var node, out var invalid))
            {
                previews[i] = previews[i] with { Diagnostics = invalid.Diagnostics };
                return new SeedBatchImportResult(previews, invalid.Outcome);
            }

            changes.Add(new PlannedChange(BatchOp.Update, plans[i].File.Seed, plans[i].File.Hash ?? plans[i].Document.Hash, node, DeleteResolution.Refuse));
            changedAt.Add(i);
        }

        var hashes = plans.Select(p => (string?)p.Document.Hash).ToList();
        if (changes.Count > 0)
        {
            var batch = await ExecuteAsync(changes, source, ct).ConfigureAwait(false);
            for (var j = 0; j < changedAt.Count; j++)
            {
                var item = batch.Items[j];
                if (batch.Outcome != SaveOutcome.Saved)
                    previews[changedAt[j]] = previews[changedAt[j]] with { Diagnostics = item.Diagnostics };
                else
                    hashes[changedAt[j]] = item.Hash;
            }

            if (batch.Outcome != SaveOutcome.Saved)
                return new SeedBatchImportResult(previews, batch.Outcome);
        }

        var outcome = SaveOutcome.Saved;
        for (var i = 0; i < plans.Count; i++)
        {
            var written = await WriteImportTranslationsAsync(previews[i], hashes[i], plans[i].File.Seed, plans[i].Plan.Translations, source, ct).ConfigureAwait(false);
            previews[i] = written.Preview;
            if (written.Outcome != SaveOutcome.Saved && outcome == SaveOutcome.Saved)
                outcome = written.Outcome;
        }

        return new SeedBatchImportResult(previews, outcome);
    }

    /// <summary>A seed import worked out against a snapshot: the preview, the seed's new JSON when its rows change, the translations.</summary>
    private sealed record SeedImportPlan(ImportPreview Preview, JsonObject? Json, List<(string Locale, string Id, string Field, string Value)> Translations);

    private SeedImportPlan PlanSeedImport(ModelSnapshot snapshot, Seed seed, ElementDocument document, string csv, bool replace)
    {
        var table = Csv.Read(csv);
        if (table.Count == 0)
            throw new FormatException("The CSV is empty; the first line must be the header.");
        var header = table[0];
        var columns = SeedColumns(snapshot, seed);
        var l10n = snapshot.Localization;
        var ignored = new List<string>();
        var map = new Dictionary<int, int>(); // CSV column -> seed column
        var localized = new List<(int At, string Locale, string Field)>();
        var idAt = -1;
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i];
            var seedAt = columns.FindIndex(c => c.Header == name);
            if (name == "@id")
                idAt = i;
            else if (seedAt >= 0)
                map[i] = seedAt;
            else if (name.StartsWith("@label:", StringComparison.Ordinal) && l10n.IsTranslated(name[7..]))
                localized.Add((i, name[7..], LocalizationIndex.LabelField));
            else if (name.StartsWith("@description:", StringComparison.Ordinal) && l10n.IsTranslated(name[13..]))
                localized.Add((i, name[13..], LocalizationIndex.DescriptionField));
            else
                ignored.Add(name);
        }

        var codeAt = snapshot.Get<Element>(seed.Target) is ReferenceType ? columns.FindIndex(c => c.Key == "code") : -1;
        var byId = seed.Rows.Select((r, i) => (r, i)).ToDictionary(p => p.r.Id, p => p.i, StringComparer.Ordinal);
        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        if (codeAt >= 0)
        {
            for (var i = 0; i < seed.Rows.Count; i++)
            {
                if (codeAt < seed.Rows[i].Values.Count && seed.Rows[i].Values[codeAt] is { ValueKind: JsonValueKind.String } code)
                    byCode.TryAdd(code.GetString()!, i);
            }
        }

        var codeCsvAt = codeAt >= 0 ? map.Where(p => p.Value == codeAt).Select(p => p.Key).DefaultIfEmpty(-1).First() : -1;
        var rows = seed.Rows.Select(r => (r.Id, Cells: Enumerable.Range(0, columns.Count).Select(i => i < r.Values.Count ? ToNode(r.Values[i]) : null).ToList())).ToList();
        var seen = new HashSet<int>();
        var newRows = new List<(string Id, List<JsonNode?> Cells)>();
        var changed = new List<JsonObject>();
        var translations = new List<(string Locale, string Id, string Field, string Value)>();
        foreach (var line in table.Skip(1))
        {
            string Cell(int at) => at < line.Count ? line[at] : "";
            int? existing = idAt >= 0 && byId.TryGetValue(Cell(idAt), out var hit) ? hit : null;
            if (existing is null && codeCsvAt >= 0 && byCode.TryGetValue(Cell(codeCsvAt), out var coded))
                existing = coded;
            string rowId;
            if (existing is { } index)
            {
                seen.Add(index);
                rowId = rows[index].Id;
                var cells = rows[index].Cells;
                JsonObject before = [], after = [];
                foreach (var (csvAt, seedAt) in map)
                {
                    var value = CellNode(Cell(csvAt), columns[seedAt]);
                    if (JsonNode.DeepEquals(value, cells[seedAt]))
                        continue;
                    before[columns[seedAt].Header] = cells[seedAt]?.DeepClone();
                    after[columns[seedAt].Header] = value?.DeepClone();
                    cells[seedAt] = value;
                }

                if (after.Count > 0)
                    changed.Add(new JsonObject { ["id"] = rowId, ["before"] = before, ["after"] = after });
            }
            else
            {
                rowId = idAt >= 0 && IsUlid(Cell(idAt)) ? Cell(idAt) : _options.EffectiveIdGenerator.NewId();
                var cells = Enumerable.Repeat<JsonNode?>(null, columns.Count).ToList();
                foreach (var (csvAt, seedAt) in map)
                    cells[seedAt] = CellNode(Cell(csvAt), columns[seedAt]);
                newRows.Add((rowId, cells));
            }

            foreach (var (at, locale, field) in localized)
            {
                var text = Cell(at);
                if (text.Length > 0 && l10n.Text(locale, rowId, field) != text)
                {
                    translations.Add((locale, rowId, field, text));
                    changed.Add(new JsonObject { ["id"] = rowId, ["locale"] = locale, ["field"] = field, ["before"] = l10n.Text(locale, rowId, field), ["after"] = text });
                }
            }
        }

        var blocked = new List<JsonObject>();
        var removed = new HashSet<int>();
        if (replace)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (seen.Contains(i))
                    continue;
                var referrers = snapshot.ReferencesTo(rows[i].Id).Where(r => r.FromElementId != seed.Id && !r.Owning).ToList();
                if (referrers.Count > 0)
                    blocked.Add(new JsonObject { ["id"] = rows[i].Id, ["referrers"] = new JsonArray([.. referrers.Select(r => (JsonNode)new JsonObject { ["element"] = r.FromElementId, ["pointer"] = r.JsonPointer })]) });
                else
                    removed.Add(i);
            }
        }

        var preview = new ImportPreview(newRows.Count, changed, removed.Count, [])
        {
            IgnoredHeaders = ignored,
            Blocked = blocked,
        };
        var seedChanges = newRows.Count > 0 || removed.Count > 0 || changed.Any(c => !c.ContainsKey("locale"));
        if (!seedChanges)
            return new SeedImportPlan(preview, null, translations);
        var json = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        var array = new JsonArray();
        foreach (var (row, i) in rows.Select((r, i) => (r, i)).Where(p => !removed.Contains(p.i)).Select(p => (p.r, p.i)))
            array.Add(RowNode(row.Id, row.Cells));
        foreach (var row in newRows)
            array.Add(RowNode(row.Id, row.Cells));
        json["rows"] = array;
        return new SeedImportPlan(preview, json, translations);
    }

    /// <summary>Writes an applied import's translations, one save per locale, and completes its preview.</summary>
    private async Task<ImportResult> WriteImportTranslationsAsync(ImportPreview preview, string? hash, string seedId,
        List<(string Locale, string Id, string Field, string Value)> translations, ChangeSource source, CancellationToken ct)
    {
        var shardHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in translations.GroupBy(t => t.Locale, StringComparer.Ordinal))
        {
            var page = await GetTranslationsAsync(group.Key, seedId, null, ct).ConfigureAwait(false);
            var written = await SaveTranslationsAsync(group.Key, [.. group.Select(t => new TranslationEdit(t.Id, t.Field, t.Value))], page.ShardHashes, source, ct).ConfigureAwait(false);
            if (written.Outcome != SaveOutcome.Saved)
                return new ImportResult(preview with { Hash = hash, Diagnostics = written.Diagnostics }, written.Outcome);
            foreach (var (path, value) in written.ShardHashes)
                shardHashes[path] = value;
        }

        return new ImportResult(preview with { Applied = true, Hash = hash, ShardHashes = shardHashes }, SaveOutcome.Saved);
    }

    /// <summary>The attributes typed by a reference type, with their effective storage per database.</summary>
    /// <param name="referenceTypeId">The reference type id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The usages, or <see langword="null"/> when no reference type has the id.</returns>
    public async Task<ReferenceTypeUsage?> GetReferenceTypeUsageAsync(string referenceTypeId, CancellationToken ct)
    {
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Element>(referenceTypeId) is not ReferenceType)
            return null;
        Resolution.ResolvedModel resolved;
        try
        {
            resolved = await _services.Resolver.ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReferenceTypeUsage([]); // a model the resolver cannot take: validation says why
        }

        var packages = snapshot.Summaries().ToDictionary(s => s.Id, s => s.Package, StringComparer.Ordinal);
        var type = resolved.ReferenceTypes.FirstOrDefault(t => t.Id == referenceTypeId);
        var usages = (type?.UsedBy.AsEnumerable() ?? []).Select(a =>
        {
            var owner = (a.Owner as Resolution.RElement)?.Id ?? "";
            var storage = (a.Reference?.Storage ?? new Dictionary<string, Resolution.RStorageChoice>())
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => new StorageChoice
                {
                    Strategy = p.Value.Strategy,
                    Options = p.Value.Options.ToDictionary(o => o.Key, o => JsonSerializer.SerializeToElement(o.Value), StringComparer.Ordinal),
                }, StringComparer.Ordinal);
            return new ReferenceTypeUse(a.Id, owner, packages.GetValueOrDefault(owner), a.Reference?.IsCollection ?? false, a.Reference?.Required ?? false, storage);
        }).OrderBy(u => u.Owner, StringComparer.Ordinal).ThenBy(u => u.Attribute, StringComparer.Ordinal).ToList();
        return new ReferenceTypeUsage(usages);
    }

    private sealed record SeedColumn(string Key, string Header, string? Type, bool Collection, bool Json);

    private static List<SeedColumn> SeedColumns(ModelSnapshot snapshot, Seed seed)
    {
        var columns = new List<SeedColumn>(seed.Columns.Count);
        foreach (var key in seed.Columns)
        {
            if (key is "code" or "label" or "description")
            {
                columns.Add(new SeedColumn(key, "@" + key, "string", false, false));
                continue;
            }

            if (!snapshot.TryGetEntry(key, out var entry) || snapshot.GetDocument(entry.OwnerId) is not { } owner || Pointer(owner.Json, entry.JsonPointer) is not { ValueKind: JsonValueKind.Object } node)
            {
                columns.Add(new SeedColumn(key, key, null, false, false));
                continue;
            }

            string? Text(string name) => node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (entry.Kind == "end")
            {
                columns.Add(new SeedColumn(key, Text("role") ?? Text("name") ?? key, "string", false, false));
                continue;
            }

            string? type = null;
            var json = false;
            if (node.TryGetProperty("type", out var t))
            {
                if (t.ValueKind == JsonValueKind.String)
                    type = t.GetString();
                else if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.String)
                    json = snapshot.Get<Element>(r.GetString()!) is { Kind: ElementKind.ValueObject };
            }

            var collection = node.TryGetProperty("collection", out var c) && c.ValueKind == JsonValueKind.True;
            columns.Add(new SeedColumn(key, Text("name") ?? key, type, collection, json));
        }

        return columns;
    }

    private static JsonElement? Pointer(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next))
                current = next;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var at) && at < current.GetArrayLength())
                current = current[at];
            else
                return null;
        }

        return current;
    }

    private static string CellText(JsonElement? cell, SeedColumn column) => cell switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
        { ValueKind: JsonValueKind.String } v => v.GetString()!,
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v => v.GetRawText(),
        { ValueKind: JsonValueKind.Array } v when !column.Json && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String) =>
            string.Join(';', v.EnumerateArray().Select(x => x.GetString())),
        { } v => JsonSerializer.Serialize(v),
    };

    private static JsonNode? CellNode(string text, SeedColumn column)
    {
        if (text.Length == 0)
            return null;
        if (column.Json || (column.Type is null && text[0] is '{' or '['))
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return JsonValue.Create(text); // kept as text: MQ7004 reports it on save
            }
        }

        if (column.Collection)
            return new JsonArray([.. text.Split(';').Select(t => (JsonNode?)JsonValue.Create(t.Trim()))]);
        if (column.Type is { } type && NumericTypes.Contains(type) && IsJsonNumber(text))
            return JsonNode.Parse(text);
        if (column.Type == "boolean" && text is "true" or "false")
            return JsonValue.Create(text == "true");
        return JsonValue.Create(text);
    }

    private static bool IsJsonNumber(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsUlid(string text) => text.Length == 26 && text.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') && c is not ('I' or 'L' or 'O' or 'U'));

    private static JsonNode? ToNode(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : JsonNode.Parse(value.GetRawText());

    private static JsonObject RowNode(string id, List<JsonNode?> cells)
    {
        var count = cells.Count;
        while (count > 0 && cells[count - 1] is null)
            count--;
        var row = new JsonObject { ["id"] = id };
        if (count > 0)
            row["values"] = new JsonArray([.. cells.Take(count).Select(c => c?.DeepClone())]);
        return row;
    }
}
