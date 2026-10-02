using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary><c>POST /api/validate</c> and <c>GET /api/validation/rules</c>; the extension files (custom property schemas and script rules) follow.</summary>
public static class ValidationEndpoints
{
    /// <summary>The built-in rule catalog: id, default severity, description, family and whether <c>validation.rules</c> may turn it off.</summary>
    /// <param name="context">The request.</param>
    /// <returns>200 with the rules, ordered by id.</returns>
    [HttpGet("/api/validation/rules")]
    public static Task<IResult> ListRules(HttpContext context) => Api.GuardAsync(context, () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Api.Json(RuleCatalog.Describe()));
    });

    /// <summary>Validates the whole model (an empty body or <c>{}</c>) or a scope; load diagnostics included, never truncated.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the report, or 400.</returns>
    [HttpPost("/api/validate")]
    public static Task<IResult> Validate(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var (scope, error) = await Api.ReadJsonAsync(context.Request, ValidationScope.All, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (scope!.ElementIds is { } ids && ids.Any(id => !Api.IsUlid(id)))
            return Api.BadRequest("elementIds must be element ids (uppercase ULIDs).");
        return Api.Json(await store.ValidateAsync(scope, ct).ConfigureAwait(false));
    });
}

/// <summary>The body of <c>PUT /api/extensions/file</c>.</summary>
/// <param name="Text">The file's UTF-8 text.</param>
public sealed record ExtensionFileWrite(string? Text);

/// <summary>
/// The model's <c>extensions/</c> folder for the editor's Extensions screen: the custom property schemas (<c>extensions/*.json</c>) and the
/// script rules (<c>extensions/rules/*.js</c>), edited in place through the engine's write guard. Reads need viewer, writes maintainer;
/// every write takes <c>If-Match</c> (or <c>If-None-Match: *</c> to create) and answers 409 with the disk hash and text when the file
/// changed. The path travels as the <c>path</c> query parameter (<c>retention.json</c>, <c>rules/naming.js</c>). A write publishes
/// <c>project.changed</c> (the editor reloads the project's extension schemas) and starts a validation, so a rule's findings follow.
/// </summary>
public static class ExtensionEndpoints
{
    /// <summary>The schemas and rules with their hashes and what is wrong with each on its own.</summary>
    [HttpGet("/api/extensions/files")]
    public static Task<IResult> List(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(await store.ListExtensionFilesAsync(ct).ConfigureAwait(false));
    });

    /// <summary>One extension file's text with its ETag.</summary>
    [HttpGet("/api/extensions/file")]
    public static Task<IResult> Get(HttpContext context, string? path, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrEmpty(path))
            return Api.BadRequest("path is required.");
        var file = await store.ReadExtensionFileAsync(path, ct).ConfigureAwait(false);
        if (file is null)
            return Api.NotFound("extension file", path);
        Api.SetETag(context, file.Hash);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(file);
    }));

    /// <summary>
    /// Writes one extension file (<c>If-Match</c>, or <c>If-None-Match: *</c> to create). A schema that is not valid is 422 with nothing
    /// written; a rule script is saved and its syntax and load check returned.
    /// </summary>
    [HttpPut("/api/extensions/file")]
    public static Task<IResult> Put(HttpContext context, string? path, ModelStore store, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (string.IsNullOrEmpty(path))
            return Api.BadRequest("path is required.");
        var create = string.Equals(context.Request.Headers.IfNoneMatch.ToString().Trim(), "*", StringComparison.Ordinal);
        string? expected = null;
        if (!create && !Api.TryGetIfMatch(context.Request, out expected))
            return Api.PreconditionRequired();
        var (request, error) = await Api.ReadJsonAsync<ExtensionFileWrite>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Text is null)
            return Api.BadRequest("text is required.");
        var result = await store.WriteExtensionFileAsync(path, request.Text, create ? null : expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            await ChangedAsync(store, events, ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome, created: create));
    }));

    /// <summary>Deletes one extension file (<c>If-Match</c>).</summary>
    [HttpDelete("/api/extensions/file")]
    public static Task<IResult> Delete(HttpContext context, string? path, ModelStore store, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (string.IsNullOrEmpty(path))
            return Api.BadRequest("path is required.");
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var result = await store.DeleteExtensionFileAsync(path, expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
            await ChangedAsync(store, events, ct).ConfigureAwait(false);
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }));

    /// <summary>Renames one extension file within its kind (<c>If-Match</c> for the source); the target must not exist.</summary>
    [HttpPost("/api/extensions/file/move")]
    public static Task<IResult> Move(HttpContext context, ModelStore store, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, () => RefusedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var (request, error) = await Api.ReadJsonAsync<ExtensionFileMove>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (string.IsNullOrEmpty(request!.From) || string.IsNullOrEmpty(request.To))
            return Api.BadRequest("from and to are required.");
        var result = await store.MoveExtensionFileAsync(request, expected, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            await ChangedAsync(store, events, ct).ConfigureAwait(false);
        }
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }));

    /// <summary>project.changed (the extension schemas are part of the project) and a validation, as the watcher does for a disk edit.</summary>
    private static async Task ChangedAsync(ModelStore store, EditorEvents events, CancellationToken ct)
    {
        if (store.Current is { } snapshot)
            await events.OnProjectFilesChangedAsync(snapshot.SettingsHash, ct).ConfigureAwait(false);
    }

    /// <summary>Maps a refused path (<see cref="ExtensionPathException"/>) to 400.</summary>
    private static async Task<IResult> RefusedAsync(Func<Task<IResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (ExtensionPathException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    }
}
