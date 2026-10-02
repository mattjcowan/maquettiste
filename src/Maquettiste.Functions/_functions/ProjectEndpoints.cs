using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The project as the editor's shell and Settings workspace see it (composed from the snapshot, the pack list and git).</summary>
/// <param name="Name"><c>settings.name</c>, else the repository folder's name.</param>
/// <param name="FormatVersion">The model format version.</param>
/// <param name="EngineVersion">The engine contract version (<see cref="Engine.EngineVersion.Value"/>), not the release.</param>
/// <param name="ProductVersion">The release running (<see cref="Engine.EngineVersion.Product"/>), such as <c>0.5.3</c>.</param>
/// <param name="Build">The build of that release (<see cref="Engine.EngineVersion.Build"/>).</param>
/// <param name="Workspace">What the checkout is called: <c>MAQUETTISTE_WORKSPACE</c>, else the git branch, else the short commit of a
/// detached head, else the linked worktree's name (<see cref="WorkspaceInfo"/>).</param>
/// <param name="Branch">The git branch, when it can be read from <c>.git</c>.</param>
/// <param name="Worktree">The linked worktree's name, or <see langword="null"/> for a main checkout.</param>
/// <param name="Repository">The main checkout's folder name, for a linked worktree.</param>
/// <param name="Mode"><c>local</c> or <c>hosted</c>.</param>
/// <param name="Settings">The typed settings.</param>
/// <param name="SettingsHash">The hash of <c>maquettiste.json</c>.</param>
/// <param name="Databases">The summaries of the database elements.</param>
/// <param name="Packs">Every pack under <c>templates/</c>, enabled or not (E2).</param>
/// <param name="PackDiagnostics">The packs' load diagnostics.</param>
/// <param name="Extensions">The extension schemas.</param>
/// <param name="Git">The git status, or <see langword="null"/> outside a git checkout.</param>
/// <param name="IconHash">The content hash of the icon <c>GET /api/project/branding/icon</c> serves, or <see langword="null"/> without one.</param>
/// <param name="ProjectKey">A stable identity of the served checkout (<see cref="ProjectEndpoints.KeyOf"/>), which the editor keys its page state by.</param>
public sealed record ProjectInfo(string Name, int FormatVersion, string EngineVersion, string ProductVersion, string Build, string? Workspace,
    string? Branch, string? Worktree, string? Repository, string Mode, ProjectSettings Settings, string SettingsHash,
    IReadOnlyList<ElementSummary> Databases, IReadOnlyList<PackManifest> Packs, IReadOnlyList<Diagnostic> PackDiagnostics,
    IReadOnlyList<ExtensionSchema> Extensions, GitSummary? Git, string? IconHash = null, string? ProjectKey = null);

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
        var icon = await store.ReadBrandingIconAsync(ct).ConfigureAwait(false);
        var name = snapshot.Settings.Name is { Length: > 0 } n ? n : Path.GetFileName(settings.Engine.RepoRoot);
        var variables = auth.VariablesFor(context);
        var where = WorkspaceInfo.Detect(settings.Engine.RepoRoot, variables.Get(EditorSettings.WorkspaceVariable));
        return Api.Json(new ProjectInfo(name, snapshot.Settings.FormatVersion, Engine.EngineVersion.Value, Engine.EngineVersion.Product,
            Engine.EngineVersion.Build, where.Workspace, where.Branch, where.Worktree, where.Repository, EditorSettings.ModeOf(variables),
            snapshot.Settings, snapshot.SettingsHash, [.. snapshot.Summaries().Where(s => s.Kind == "database")], packs.Packs, packs.Diagnostics,
            [.. snapshot.Extensions.Select(e => e.Schema)], status, icon?.Hash, KeyOf(settings.Engine.RepoRoot)));
    });

    /// <summary>The first 16 hex digits of the SHA-256 of the full root path: the same checkout keeps its key across a rename of
    /// <c>settings.name</c>, and two checkouts with one name get two keys.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <returns>16 lowercase hex digits.</returns>
    public static string KeyOf(string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full));
        return Convert.ToHexStringLower(hash)[..16];
    }

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
