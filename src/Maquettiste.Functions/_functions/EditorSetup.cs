using System.Security.Cryptography;
using System.Text;
using Maquettiste.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>Registers the editor's services (phase2-design.md section 3.5).</summary>
public static class EditorSetup
{
    /// <summary>
    /// Registers singletons only: <see cref="EditorSettings"/>, its <see cref="EngineOptions"/>, the <see cref="ModelStore"/> (no I/O,
    /// never throws for model content), the <see cref="GenerationService"/>, the <see cref="JobQueue"/> (16 queued jobs), the sign-in
    /// state, the realtime publisher, the presence registry and the git reader. The container disposes the store and the queue.
    /// </summary>
    /// <param name="services">The functions' services.</param>
    /// <param name="variables">The site's variables.</param>
    /// <param name="data">The site's data folder, which is the model root (the compose file mounts <c>.maquettiste/</c> there).</param>
    /// <param name="logger">The functions' logger.</param>
    [ConfigureServices]
    public static void Configure(IServiceCollection services, ISiteVariables variables, DirectoryInfo data, ILogger logger)
    {
        var settings = EditorSettings.From(variables, data);
        if (variables.Get(EditorSettings.TokenVariable).Length > 0 && EditorSettings.LocalTrustOn(variables))
        {
            logger.LogWarning(
                "maquettiste: an editor token is set while local trust is on; if this port is reachable through a reverse proxy that " +
                "rewrites Host to a local name, set MAQUETTISTE_LOCAL_TRUST=off.");
        }

        services.AddSingleton(settings);
        services.AddSingleton(settings.Engine);
        services.AddSingleton(sp => new ModelStore(sp.GetRequiredService<EngineOptions>()));
        services.AddSingleton(sp => new GenerationService(sp.GetRequiredService<ModelStore>(), sp.GetRequiredService<EngineOptions>()));
        services.AddSingleton(sp => new JobQueue(sp.GetRequiredService<GenerationService>(), sp.GetRequiredService<EngineOptions>(), capacity: 16));
        services.AddSingleton<EditorAuth>();
        services.AddSingleton<EditorEvents>();
        services.AddSingleton<PresenceRegistry>();
        services.AddSingleton<GitStatusReader>();
    }
}

/// <summary>What the site variables say, built once per load without I/O (phase2-design.md section 3.2).</summary>
public sealed class EditorSettings
{
    /// <summary>The variable naming the mode: <c>local</c> (also when empty) or <c>hosted</c>.</summary>
    public const string ModeVariable = "MAQUETTISTE_MODE";

    /// <summary>The variable naming the repository root.</summary>
    public const string RepoRootVariable = "MAQUETTISTE_REPO_ROOT";

    /// <summary>The secret variable holding the editor token.</summary>
    public const string TokenVariable = "MAQUETTISTE_EDITOR_TOKEN";

    /// <summary>The variable naming the index cache root.</summary>
    public const string CacheVariable = "MAQUETTISTE_CACHE_DIR";

    /// <summary>The variable listing the addresses trusted as the local developer.</summary>
    public const string LocalPeersVariable = "MAQUETTISTE_LOCAL_PEERS";

    /// <summary>The variable that turns local trust off.</summary>
    public const string LocalTrustVariable = "MAQUETTISTE_LOCAL_TRUST";

    /// <summary>The variable naming the local developer.</summary>
    public const string LocalUserVariable = "MAQUETTISTE_LOCAL_USER";

    /// <summary>The engine options.</summary>
    public required EngineOptions Engine { get; init; }

    /// <summary>The cache root the engine's <see cref="EngineOptions.CacheDirectory"/> is a folder of.</summary>
    public required string CacheRoot { get; init; }

    /// <summary>Whether <see cref="EngineOptions.RepoRoot"/> came from <c>MAQUETTISTE_REPO_ROOT</c> (else the data folder's parent).</summary>
    public required bool RepoRootConfigured { get; init; }

    /// <summary>
    /// Builds the settings: <c>RepoRoot</c> from <c>MAQUETTISTE_REPO_ROOT</c> (else the data folder's parent), <c>ModelRoot</c> the data
    /// folder, <c>CacheDirectory</c> <c>&lt;cache root&gt;/&lt;first 16 hex of SHA-256(full RepoRoot)&gt;</c>, parallelism 0.
    /// </summary>
    /// <param name="variables">The site's variables.</param>
    /// <param name="data">The site's data folder.</param>
    /// <returns>The settings.</returns>
    public static EditorSettings From(ISiteVariables variables, DirectoryInfo data)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(data);
        var configured = variables.Get(RepoRootVariable).Trim();
        var repoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.Length > 0
            ? configured
            : data.Parent?.FullName ?? data.FullName));
        var cacheRoot = variables.Get(CacheVariable).Trim();
        if (cacheRoot.Length == 0)
            cacheRoot = Path.Combine(Path.GetTempPath(), "maquettiste-cache");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot)))[..16];
        return new EditorSettings
        {
            Engine = new EngineOptions
            {
                RepoRoot = repoRoot,
                ModelRoot = data.FullName,
                CacheDirectory = Path.Combine(cacheRoot, key),
                MaxDegreeOfParallelism = 0,
            },
            CacheRoot = cacheRoot,
            RepoRootConfigured = configured.Length > 0,
        };
    }

    /// <summary>The mode the variables name now: <c>hosted</c> or <c>local</c>.</summary>
    /// <param name="variables">The variables.</param>
    /// <returns>The mode.</returns>
    public static string ModeOf(ISiteVariables variables) =>
        string.Equals(variables.Get(ModeVariable).Trim(), "hosted", StringComparison.OrdinalIgnoreCase) ? "hosted" : "local";

    /// <summary>Whether local trust is on: local mode and <c>MAQUETTISTE_LOCAL_TRUST</c> is not <c>off</c>.</summary>
    /// <param name="variables">The variables.</param>
    /// <returns><see langword="true"/> when a local peer is the local developer.</returns>
    public static bool LocalTrustOn(ISiteVariables variables) =>
        ModeOf(variables) == "local" && !string.Equals(variables.Get(LocalTrustVariable).Trim(), "off", StringComparison.OrdinalIgnoreCase);
}
