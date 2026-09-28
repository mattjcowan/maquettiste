using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The project as the editor's shell and Settings workspace see it (composed from the snapshot, the pack list and git).</summary>
/// <param name="Name"><c>settings.name</c>, else the repository folder's name.</param>
/// <param name="FormatVersion">The model format version.</param>
/// <param name="EngineVersion">The engine's format-level version.</param>
/// <param name="Mode"><c>local</c> or <c>hosted</c>.</param>
/// <param name="Settings">The typed settings.</param>
/// <param name="SettingsHash">The hash of <c>maquettiste.json</c>.</param>
/// <param name="Databases">The summaries of the database elements.</param>
/// <param name="Packs">Every pack under <c>templates/</c>, enabled or not (E2).</param>
/// <param name="PackDiagnostics">The packs' load diagnostics.</param>
/// <param name="Extensions">The extension schemas.</param>
/// <param name="Git">The git status, or <see langword="null"/> outside a git checkout.</param>
public sealed record ProjectInfo(string Name, int FormatVersion, string EngineVersion, string Mode, ProjectSettings Settings, string SettingsHash,
    IReadOnlyList<ElementSummary> Databases, IReadOnlyList<PackManifest> Packs, IReadOnlyList<Diagnostic> PackDiagnostics,
    IReadOnlyList<ExtensionSchema> Extensions, GitSummary? Git);

/// <summary><c>GET /api/project</c> and <c>GET</c>, <c>PUT /api/project/settings</c>.</summary>
public static class ProjectEndpoints
{
    /// <summary>Settings, format version, databases, installed packs, extension schemas and git status.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="generation">The generation service (pack list).</param>
    /// <param name="settings">The editor settings.</param>
    /// <param name="git">The git reader.</param>
    /// <param name="auth">The sign-in state (the mode in force).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with <see cref="ProjectInfo"/>.</returns>
    [HttpGet("/api/project")]
    public static Task<IResult> Get(HttpContext context, ModelStore store, GenerationService generation, EditorSettings settings, GitStatusReader git,
        EditorAuth auth, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(auth);
        var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var packs = await generation.GetPacksAsync(ct).ConfigureAwait(false);
        var status = await git.ReadAsync(ct).ConfigureAwait(false);
        var name = snapshot.Settings.Name is { Length: > 0 } n ? n : Path.GetFileName(settings.Engine.RepoRoot);
        return Api.Json(new ProjectInfo(name, snapshot.Settings.FormatVersion, Engine.EngineVersion.Value, EditorSettings.ModeOf(auth.VariablesFor(context)),
            snapshot.Settings, snapshot.SettingsHash, [.. snapshot.Summaries().Where(s => s.Kind == "database")], packs.Packs, packs.Diagnostics,
            [.. snapshot.Extensions.Select(e => e.Schema)], status));
    });

    /// <summary>The canonical <c>maquettiste.json</c> with its hash as ETag (E3).</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the settings document.</returns>
    [HttpGet("/api/project/settings")]
    public static Task<IResult> GetSettings(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var document = await store.GetSettingsAsync(ct).ConfigureAwait(false);
        Api.SetETag(context, document.Hash);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(document);
    });

    /// <summary>Saves <c>maquettiste.json</c> with <c>If-Match</c> (E3); a save publishes <c>project.changed</c> and revalidates.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="events">The publisher.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 428 or 400.</returns>
    [HttpPut("/api/project/settings")]
    public static Task<IResult> SaveSettings(HttpContext context, ModelStore store, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        if (Api.Require(context, "admin") is { } forbidden)
            return forbidden;
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var (body, error) = await Api.ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var result = await store.SaveSettingsAsync(body, expected, ChangeSource.Editor, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            await events.OnSettingsChangedAsync(result.Hash!, ct).ConfigureAwait(false);
        }

        return Api.Json(result, Api.StatusOf(result.Outcome));
    });
}
