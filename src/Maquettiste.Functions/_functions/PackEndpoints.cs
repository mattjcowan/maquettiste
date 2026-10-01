using System.Text.Json;
using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The body of <c>GET /api/packs</c>.</summary>
/// <param name="Packs">Every pack folder, enabled or not, ordinal by name.</param>
public sealed record PackListResponse(IReadOnlyList<PackSummary> Packs);

/// <summary>The body of <c>POST /api/packs</c>.</summary>
/// <param name="Name">The new pack's name (its folder).</param>
/// <param name="From"><c>empty</c> (the default) or the name of a pack of this project to copy.</param>
public sealed record NewPackRequest(string? Name, string? From);

/// <summary>The body of <c>POST /api/packs/{pack}/rename</c>.</summary>
/// <param name="Name">The new name (the folder it moves to).</param>
/// <param name="DryRun">Check everything and report what would move (with the generation hints that name the pack), writing nothing.</param>
public sealed record PackRenameRequest(string? Name, bool DryRun = false);

/// <summary>The body of <c>PUT /api/packs/{pack}/file</c>.</summary>
/// <param name="Text">The file's UTF-8 text.</param>
public sealed record PackFileWrite(string? Text);

/// <summary>
/// Pack authoring (generation-ui.md section 5.1): the editor edits the files under <c>.maquettiste/templates/&lt;pack&gt;/</c> in place
/// (GU1). Reads need viewer, writes maintainer; every write takes <c>If-Match</c> (or <c>If-None-Match: *</c> to create a file) and
/// answers 409 with the disk hash and text when the file changed. File paths travel as the <c>path</c> query parameter.
/// </summary>
public static class PackEndpoints
{
    /// <summary>Every pack with its units and load diagnostics.</summary>
    [HttpGet("/api/packs")]
    public static Task<IResult> List(HttpContext context, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(generation);
        return Api.Json(new PackListResponse(await generation.ListPacksAsync(ct).ConfigureAwait(false)));
    });

    /// <summary>Creates a pack from <c>empty</c> or a pack of this project; 409 when the folder exists.</summary>
    [HttpPost("/api/packs")]
    public static Task<IResult> Create(HttpContext context, GenerationService generation, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        var (request, error) = await Api.ReadJsonAsync<NewPackRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (string.IsNullOrEmpty(request!.Name))
            return Api.BadRequest("name is required.");
        var result = await generation.CreatePackAsync(request.Name, request.From ?? "empty", null, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            await events.OnPackFilesChangedAsync([new TemplatesChangedEvent(request.Name, [])], ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome, created: true));
    }));

    /// <summary><c>pack.json</c> as a document with its ETag, the parameters, the files and the diagnostics.</summary>
    [HttpGet("/api/packs/{pack}")]
    public static Task<IResult> Get(HttpContext context, string pack, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        var document = await generation.GetPackAsync(pack, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("pack", pack);
        Api.SetETag(context, document.Hash);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(document);
    }));

    /// <summary>Saves the whole <c>pack.json</c> document in canonical form.</summary>
    [HttpPut("/api/packs/{pack}")]
    public static Task<IResult> Save(HttpContext context, string pack, GenerationService generation, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var (body, error) = await Api.ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var result = await generation.SavePackAsync(pack, body, expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            await events.OnPackFilesChangedAsync([new TemplatesChangedEvent(pack, [new TemplateFileHash("pack.json", result.Hash)])], ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }));

    /// <summary>
    /// Removes a pack (<c>If-Match</c>: the <c>pack.json</c> hash): its folder, its <c>packs.&lt;pack&gt;</c> settings entry and its manifests.
    /// The files it generated stay on disk, untracked.
    /// </summary>
    [HttpDelete("/api/packs/{pack}")]
    public static Task<IResult> Delete(HttpContext context, string pack, GenerationService generation, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(events);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var result = await generation.DeletePackAsync(pack, expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            if (result.SettingsHash is { } settingsHash)
                await events.OnSettingsChangedAsync(settingsHash, ct).ConfigureAwait(false);
            await events.OnPackFilesChangedAsync([new TemplatesChangedEvent(pack, [])], ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }));

    /// <summary>
    /// Renames a pack (<c>If-Match</c>: the <c>pack.json</c> hash): its folder, its <c>packs.&lt;pack&gt;</c> settings entry, its manifests and
    /// unit states move to the new name together, so the files it generated stay tracked. The new <c>pack.json</c> hash is the ETag.
    /// </summary>
    [HttpPost("/api/packs/{pack}/rename")]
    public static Task<IResult> Rename(HttpContext context, string pack, GenerationService generation, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(events);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var (request, error) = await Api.ReadJsonAsync<PackRenameRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Name is null)
            return Api.BadRequest("name is required.");
        var result = await generation.RenamePackAsync(pack, request.Name, expected, ct, dryRun: request.DryRun).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved && !request.DryRun)
        {
            Api.SetETag(context, result.Hash);
            if (result.SettingsHash is { } settingsHash)
                await events.OnSettingsChangedAsync(settingsHash, ct).ConfigureAwait(false);
            await events.OnPackFilesChangedAsync([new TemplatesChangedEvent(pack, []), new TemplatesChangedEvent(result.To, [])], ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }));

    /// <summary>One pack file's text with its ETag.</summary>
    [HttpGet("/api/packs/{pack}/file")]
    public static Task<IResult> GetFile(HttpContext context, string pack, string? path, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        if (string.IsNullOrEmpty(path))
            return Api.BadRequest("path is required.");
        var file = await generation.ReadPackFileAsync(pack, path, ct).ConfigureAwait(false);
        if (file is null)
            return Api.NotFound("pack file", pack + "/" + path);
        Api.SetETag(context, file.Hash);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(file);
    }));

    /// <summary>Writes one pack file (<c>If-Match</c>, or <c>If-None-Match: *</c> to create); returns the hash and parse diagnostics.</summary>
    [HttpPut("/api/packs/{pack}/file")]
    public static Task<IResult> PutFile(HttpContext context, string pack, string? path, GenerationService generation, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (string.IsNullOrEmpty(path))
            return Api.BadRequest("path is required.");
        var create = string.Equals(context.Request.Headers.IfNoneMatch.ToString().Trim(), "*", StringComparison.Ordinal);
        string? expected = null;
        if (!create && !Api.TryGetIfMatch(context.Request, out expected))
            return Api.PreconditionRequired();
        var (request, error) = await Api.ReadJsonAsync<PackFileWrite>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Text is null)
            return Api.BadRequest("text is required.");
        var result = await generation.WritePackFileAsync(pack, path, request.Text, create ? null : expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            await events.OnPackFilesChangedAsync([new TemplatesChangedEvent(pack, [new TemplateFileHash(path, result.Hash)])], ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome, created: create));
    }));

    /// <summary>Deletes one pack file; 409 while a unit names it or a template includes it.</summary>
    [HttpDelete("/api/packs/{pack}/file")]
    public static Task<IResult> DeleteFile(HttpContext context, string pack, string? path, GenerationService generation, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (string.IsNullOrEmpty(path))
            return Api.BadRequest("path is required.");
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var result = await generation.DeletePackFileAsync(pack, path, expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
            await events.OnPackFilesChangedAsync([new TemplatesChangedEvent(pack, [new TemplateFileHash(path, null)])], ct).ConfigureAwait(false);
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }));

    /// <summary>Moves one pack file (<c>If-Match</c> for the source); with <c>updateUnits</c> the units that name it are rewritten too.</summary>
    [HttpPost("/api/packs/{pack}/file/move")]
    public static Task<IResult> MoveFile(HttpContext context, string pack, GenerationService generation, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var (request, error) = await Api.ReadJsonAsync<PackFileMove>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (string.IsNullOrEmpty(request!.From) || string.IsNullOrEmpty(request.To))
            return Api.BadRequest("from and to are required.");
        var result = await generation.MovePackFileAsync(pack, request, expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            // The moved file keeps its bytes; a rewritten pack.json has a new hash the editor refetches (an empty entry would reload all).
            TemplateFileHash[] files = [new(request.From!, null), new(request.To!, result.Hash)];
            await events.OnPackFilesChangedAsync([new TemplatesChangedEvent(pack, request.UpdateUnits
                ? [.. files.Append(new TemplateFileHash("pack.json", null)).OrderBy(f => f.Path, StringComparer.Ordinal)]
                : [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)])], ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }));

    /// <summary>The files a pack's manifests record, with unit, element, root, mode and disk state.</summary>
    [HttpGet("/api/packs/{pack}/outputs")]
    public static Task<IResult> Outputs(HttpContext context, string pack, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(generation);
        var outputs = await generation.GetPackOutputsAsync(pack, ct).ConfigureAwait(false);
        return outputs is null ? Api.NotFound("pack", pack) : Api.Json(outputs);
    }));

    /// <summary>Saves <c>packs.&lt;pack&gt;</c> of <c>maquettiste.json</c> (enabled, output, parameters) with the settings ETag.</summary>
    [HttpPut("/api/project/settings/packs/{pack}")]
    public static Task<IResult> SaveSettings(HttpContext context, string pack, GenerationService generation, EditorEvents events, CancellationToken ct) =>
        Api.GuardAsync(context, () => RefusedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(generation);
            ArgumentNullException.ThrowIfNull(events);
            if (Api.Require(context, "maintainer") is { } forbidden)
                return forbidden;
            if (!Api.TryGetIfMatch(context.Request, out var expected))
                return Api.PreconditionRequired();
            var (body, error) = await Api.ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
            if (error is not null)
                return error;
            JsonElement section;
            try
            {
                using var document = JsonDocument.Parse(body);
                section = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Api.BadRequest("The body is not valid JSON.");
            }

            if (section.ValueKind != JsonValueKind.Object)
                return Api.BadRequest("The body must be a JSON object: enabled, output, parameters.");
            var result = await generation.SavePackSettingsAsync(pack, section, expected, ct).ConfigureAwait(false);
            if (result.Outcome == SaveOutcome.Saved)
            {
                Api.SetETag(context, result.Hash);
                await events.OnSettingsChangedAsync(result.Hash!, ct).ConfigureAwait(false);
            }

            return Api.Json(result, Api.StatusOf(result.Outcome));
        }));

    /// <summary>Maps a refused pack name or path (<see cref="PackPathException"/>) to 400.</summary>
    private static async Task<IResult> RefusedAsync(Func<Task<IResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (PackPathException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    }
}
