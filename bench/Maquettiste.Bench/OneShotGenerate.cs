using System.Globalization;
using System.Reflection;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Bench;

/// <summary>
/// The bench app's <c>one-shot-generate</c> mode: one apply run in this fresh process, the way <c>maquettiste generate</c> runs it
/// (a new model store, the engine's last-run record on, the run lock waited for), so <c>dotnet run --project bench/Maquettiste.Bench</c>
/// can measure the one-shot figure without a packed tool (<see cref="BenchmarkOptions.OneShotCommand"/>). It takes the arguments the
/// harness appends: <c>--repo &lt;dir&gt; --cache-dir &lt;dir&gt; --jobs &lt;n&gt; generate --quiet</c>. Exit codes follow the CLI: 0
/// succeeded, 1 invalid, 2 drift, 3 conflicts, 4 anything else (usage errors included).
/// </summary>
public static class OneShotGenerate
{
    /// <summary>The first argument that selects the mode.</summary>
    public const string Verb = "one-shot-generate";

    /// <summary>The command that starts this mode in a new process of the running bench app.</summary>
    /// <returns>The program and its leading arguments: the app host, or <c>dotnet</c> and the bench assembly.</returns>
    public static IReadOnlyList<string> SelfCommand() => [.. CurrentProcessCommand(typeof(OneShotGenerate).Assembly), Verb];

    /// <summary>
    /// The command that starts the running program again: its app host, or, when it runs under the <c>dotnet</c> host, the host and
    /// the entry assembly.
    /// </summary>
    /// <param name="entry">The program's entry assembly.</param>
    /// <returns>The program and its leading arguments.</returns>
    public static IReadOnlyList<string> CurrentProcessCommand(Assembly entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
        return string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase) ? [process, entry.Location] : [process];
    }

    /// <summary>Runs one generation.</summary>
    /// <param name="args">The arguments after <see cref="Verb"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? repo = null;
        string? cache = null;
        var jobs = 8;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--repo" when i + 1 < args.Count: repo = args[++i]; break;
                case "--cache-dir" when i + 1 < args.Count: cache = args[++i]; break;
                case "--jobs" when i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0:
                    jobs = n;
                    i++;
                    break;
                case "generate" or "--quiet": break;
                default:
                    await Console.Error.WriteLineAsync("usage: Maquettiste.Bench " + Verb + " --repo <dir> --cache-dir <dir> [--jobs <n>] generate [--quiet]")
                        .ConfigureAwait(false);
                    return 4;
            }
        }

        if (repo is null || cache is null)
            return await RunAsync(["--help"], ct).ConfigureAwait(false);
        var options = new EngineOptions { RepoRoot = Path.GetFullPath(repo), CacheDirectory = Path.GetFullPath(cache), MaxDegreeOfParallelism = jobs };
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var service = new GenerationService(store, options) { ReuseLastRun = true };
            var result = await service.RunAsync(new GenerationRequest { Jobs = jobs }, null, ct).ConfigureAwait(false);
            foreach (var d in result.Diagnostics.Where(d => d.Severity == Engine.Diagnostics.DiagnosticSeverity.Error).Take(10))
                await Console.Error.WriteLineAsync(d.Rule + " " + (d.FilePath ?? d.ElementId ?? "") + ": " + d.Message).ConfigureAwait(false);
            return result.Outcome switch
            {
                RunOutcome.Succeeded => 0,
                RunOutcome.Invalid => 1,
                RunOutcome.Drift => 2,
                RunOutcome.Conflicts => 3,
                _ => 4,
            };
        }
    }
}
