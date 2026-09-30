using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>
/// Translations of the standard fields, seed CSV and reference type usage (reference-types-seeds-localization.md sections 2.3 and 3.9).
/// </summary>
public static class LocalizationEndpoints
{
    /// <summary>The settings and the completeness per locale and shard.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200.</returns>
    [HttpGet("/api/localization")]
    public static Task<IResult> Status(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        return Api.Json(await store.GetLocalizationStatusAsync(ct).ConfigureAwait(false));
    });

    /// <summary>Entries of one locale by owner, by shard, or the paged ones that need work.</summary>
    /// <param name="locale">A declared locale other than the default.</param>
    /// <param name="owner">An owner element id.</param>
    /// <param name="shard">A shard repo path.</param>
    /// <param name="missing"><c>true</c> for the paged queue.</param>
    /// <param name="cursor">The cursor of the page.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/localization/{locale}/entries")]
    public static Task<IResult> Entries(string locale, string? owner, string? shard, string? missing, string? cursor, HttpContext context, ModelStore store, CancellationToken ct) =>
        Api.GuardAsync(context, async () =>
        {
            ArgumentNullException.ThrowIfNull(store);
            if (!await store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
                return Api.NotFound("translated locale", locale);
            return Api.Json(await store.GetTranslationRowsAsync(locale, NullIfEmpty(owner), NullIfEmpty(shard), missing == "true", NullIfEmpty(cursor), ct).ConfigureAwait(false));
        });

    /// <summary>Writes, removes or confirms translations of one locale in one save, checked against the shards' hashes.</summary>
    /// <param name="locale">A declared locale other than the default.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 404 or 400.</returns>
    [HttpPut("/api/localization/{locale}/entries")]
    public static Task<IResult> Write(string locale, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        if (!await store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return Api.NotFound("translated locale", locale);
        var (request, error) = await Api.ReadJsonAsync<TranslationWriteRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Entries is not { } entries)
            return Api.BadRequest("entries is required.");
        var edits = new List<TranslationEdit>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Id is null || entry.Field is null)
                return Api.BadRequest("Each entry needs an id and a field.");
            string? value;
            switch (entry.Value)
            {
                case null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                    value = null;
                    break;
                case { ValueKind: JsonValueKind.String } text:
                    value = text.GetString();
                    break;
                default:
                    return Api.BadRequest($"The value of {entry.Id}/{entry.Field} must be a string or null.");
            }

            edits.Add(new TranslationEdit(entry.Id, entry.Field, value, entry.Confirm ?? false));
        }

        var result = await store.SaveTranslationsAsync(locale, edits, request.Expected ?? new Dictionary<string, string>(), ChangeSource.Editor, ct).ConfigureAwait(false);
        return Api.Json(new TranslationWriteResult(OutcomeName(result.Outcome), result.ShardHashes, result.Diagnostics), Api.StatusOf(result.Outcome));
    });

    /// <summary>Exports one locale as XLIFF 2.1 or CSV.</summary>
    /// <param name="locale">A declared locale other than the default.</param>
    /// <param name="format"><c>xliff</c> (default) or <c>csv</c>.</param>
    /// <param name="shard">A shard repo path, or all.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 404 or 400.</returns>
    [HttpGet("/api/localization/{locale}/export")]
    public static Task<IResult> Export(string locale, string? format, string? shard, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        format = string.IsNullOrEmpty(format) ? "xliff" : format;
        if (format is not ("xliff" or "csv"))
            return Api.BadRequest($"format must be 'xliff' or 'csv', not '{format}'.");
        if (!await store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return Api.NotFound("translated locale", locale);
        var text = await store.ExportTranslationsAsync(locale, format, NullIfEmpty(shard), ct).ConfigureAwait(false);
        return Results.Text(text, format == "csv" ? "text/csv; charset=utf-8" : "application/xliff+xml; charset=utf-8");
    });

    /// <summary>Previews (<c>dryRun</c>, the default) or applies an XLIFF or CSV translation import as one save.</summary>
    /// <param name="locale">A declared locale other than the default.</param>
    /// <param name="dryRun">Only preview (default true).</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 404 or 400.</returns>
    [HttpPost("/api/localization/{locale}/import")]
    public static Task<IResult> Import(string locale, string? dryRun, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        if (!await store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return Api.NotFound("translated locale", locale);
        var (request, error) = await Api.ReadJsonAsync<FileImportRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Format is not ("xliff" or "csv") || request.Content is null)
            return Api.BadRequest("format ('xliff' or 'csv') and content are required.");
        try
        {
            var result = await store.ImportTranslationsAsync(locale, request.Format, request.Content, dryRun != "false", ChangeSource.Editor, ct).ConfigureAwait(false);
            return Api.Json(result.Preview, Api.StatusOf(result.Outcome));
        }
        catch (FormatException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>A seed's rows as CSV.</summary>
    /// <param name="id">The seed id.</param>
    /// <param name="bom"><c>true</c> adds a byte order mark and CRLF line ends.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/seeds/{id}/csv")]
    public static Task<IResult> ExportSeed(string id, string? bom, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var locales = context.Request.Query["locale"].Where(l => !string.IsNullOrEmpty(l)).Select(l => l!).ToList();
        var text = await store.ExportSeedCsvAsync(id, bom == "true", locales, ct).ConfigureAwait(false);
        return text is null ? Api.NotFound("seed", id) : Results.Text(text, "text/csv; charset=utf-8");
    });

    /// <summary>
    /// Previews (<c>dryRun</c>, the default) or applies a CSV import into a seed; applying saves the seed with <c>If-Match</c> (or the
    /// hash as read) and then the translations of the locale columns.
    /// </summary>
    /// <param name="id">The seed id.</param>
    /// <param name="mode"><c>merge</c> (default) or <c>replace</c>.</param>
    /// <param name="dryRun">Only preview (default true).</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 404 or 400.</returns>
    [HttpPost("/api/seeds/{id}/csv")]
    public static Task<IResult> ImportSeed(string id, string? mode, string? dryRun, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        if (mode is not (null or "" or "merge" or "replace"))
            return Api.BadRequest($"mode must be 'merge' or 'replace', not '{mode}'.");
        var (request, error) = await Api.ReadJsonAsync<FileImportRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Content is null)
            return Api.BadRequest("content (the CSV text) is required.");
        string? expected = Api.TryGetIfMatch(context.Request, out var hash) ? hash : null;
        try
        {
            var result = await store.ImportSeedCsvAsync(id, request.Content, mode == "replace", dryRun != "false", expected, ChangeSource.Editor, ct).ConfigureAwait(false);
            if (result is null)
                return Api.NotFound("seed", id);
            if (result.Preview.Hash is { } saved && result.Outcome == SaveOutcome.Saved)
                Api.SetETag(context, saved);
            return Api.Json(result.Preview, Api.StatusOf(result.Outcome));
        }
        catch (FormatException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>Previews or applies CSV imports into several seeds (Import seed data…): the rows of every seed in one change, then the translations the files carry, one save per locale.</summary>
    /// <param name="mode"><c>merge</c> (default) or <c>replace</c>.</param>
    /// <param name="dryRun">Only preview (default true).</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 404 or 400.</returns>
    [HttpPost("/api/seeds/csv")]
    public static Task<IResult> ImportSeeds(string? mode, string? dryRun, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        if (mode is not (null or "" or "merge" or "replace"))
            return Api.BadRequest($"mode must be 'merge' or 'replace', not '{mode}'.");
        var (request, error) = await Api.ReadJsonAsync<SeedsImportRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Files is not { Count: > 0 } files || files.Any(f => string.IsNullOrEmpty(f.Seed) || f.Content is null))
            return Api.BadRequest("files (each with seed and content) is required.");
        try
        {
            var result = await store.ImportSeedCsvBatchAsync([.. files.Select(f => new SeedCsvFile(f.Seed!, f.Content!, f.Hash))], mode == "replace", dryRun != "false", ChangeSource.Editor, ct).ConfigureAwait(false);
            if (result is null)
                return Api.NotFound("seed", string.Join(", ", files.Select(f => f.Seed)));
            return Api.Json(new { items = result.Items }, Api.StatusOf(result.Outcome));
        }
        catch (FormatException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>The attributes typed by a reference type, with their effective storage per database.</summary>
    /// <param name="id">The reference type id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/reference-types/{id}/usage")]
    public static Task<IResult> Usage(string id, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        var usage = await store.GetReferenceTypeUsageAsync(id, ct).ConfigureAwait(false);
        return usage is null ? Api.NotFound("reference type", id) : Api.Json(usage);
    });

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string OutcomeName(SaveOutcome outcome) => outcome switch
    {
        SaveOutcome.Saved => "saved",
        SaveOutcome.Conflict => "conflict",
        _ => "invalid",
    };
}

/// <summary>The body of <c>PUT /api/localization/{locale}/entries</c>.</summary>
/// <param name="Entries">The edits.</param>
/// <param name="Expected">Shard repo path to the hash the caller loaded.</param>
public sealed record TranslationWriteRequest(IReadOnlyList<TranslationWriteEntry>? Entries, IReadOnlyDictionary<string, string>? Expected);

/// <summary>One edit of <see cref="TranslationWriteRequest"/>.</summary>
/// <param name="Id">The node id.</param>
/// <param name="Field">The field.</param>
/// <param name="Value">The text, or null to remove it.</param>
/// <param name="Confirm">Rewrites the field's source fingerprint without changing the text.</param>
public sealed record TranslationWriteEntry(string? Id, string? Field, JsonElement? Value, bool? Confirm);

/// <summary>The answer of <c>PUT /api/localization/{locale}/entries</c>.</summary>
/// <param name="Outcome">saved, conflict or invalid.</param>
/// <param name="Hashes">The new hashes of the shards written (saved) or the current hashes of the shards that changed (conflict).</param>
/// <param name="Diagnostics">Why it was refused (invalid).</param>
public sealed record TranslationWriteResult(string Outcome, IReadOnlyDictionary<string, string> Hashes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The body of an import: the file's format (translations only) and text.</summary>
/// <param name="Format"><c>xliff</c> or <c>csv</c>.</param>
/// <param name="Content">The file text.</param>
public sealed record FileImportRequest(string? Format, string? Content);

/// <summary>One file of a several-seed import.</summary>
/// <param name="Seed">The seed id.</param>
/// <param name="Content">The CSV text.</param>
/// <param name="Hash">The seed hash read for the preview.</param>
public sealed record SeedFileRequest(string? Seed, string? Content, string? Hash);

/// <summary>The body of a several-seed import.</summary>
/// <param name="Files">One CSV per seed.</param>
public sealed record SeedsImportRequest(IReadOnlyList<SeedFileRequest>? Files);
