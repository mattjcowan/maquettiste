namespace Maquettiste.Cli.Commands;

/// <summary>Shared checks of commands that need an initialized repo.</summary>
internal static class ProjectGuard
{
    /// <summary>
    /// Returns the repo root, or writes an error and returns <see langword="null"/> when it holds no <c>.maquettiste/maquettiste.json</c>
    /// (the command then exits 1, like <c>migrate</c> on a missing model).
    /// </summary>
    /// <param name="context">The global context.</param>
    /// <returns>The repo root, or <see langword="null"/>.</returns>
    public static async Task<string?> RepoAsync(GlobalContext context)
    {
        var repo = context.RepoRoot();
        var settings = Path.Combine(repo, GlobalContext.ModelFolder, "maquettiste.json");
        if (File.Exists(settings))
            return repo;
        await context.Error.WriteLineAsync($"maquettiste: no model found: {settings} does not exist (run maquettiste init, or pass --repo).").ConfigureAwait(false);
        return null;
    }
}
