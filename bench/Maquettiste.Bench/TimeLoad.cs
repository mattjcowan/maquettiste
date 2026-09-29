using System.Diagnostics;
using System.Globalization;
using Maquettiste.Engine;

namespace Maquettiste.Bench;

/// <summary>
/// The bench app's <c>time-load</c> verb (reference-types-seeds-localization.md section 5): times the cold load of a model written
/// by <c>write-model</c> (a new model store over an empty cache folder, so nothing is reused), then the completeness pass over its
/// locale shards. Compare a model written with <c>--locales 4</c> against the same model without. The model is only read.
/// </summary>
public static class TimeLoad
{
    /// <summary>The first argument that selects the verb.</summary>
    public const string Verb = "time-load";

    /// <summary>The usage text.</summary>
    public const string Usage = "usage: Maquettiste.Bench time-load --model <dir> [--rounds 3]\n";

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after <see cref="Verb"/>.</param>
    /// <param name="output">Where the timings go.</param>
    /// <param name="error">Where usage errors go.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code: 0, or 4 on a usage error.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        string? model = null;
        var rounds = 3;
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--model": model = value; i++; break;
                case "--rounds" when int.TryParse(value, CultureInfo.InvariantCulture, out var r) && r > 0: rounds = r; i++; break;
                default:
                    await error.WriteAsync($"unknown or invalid option '{args[i]}'.\n{Usage}").ConfigureAwait(false);
                    return 4;
            }
        }

        if (string.IsNullOrEmpty(model))
        {
            await error.WriteAsync(Usage).ConfigureAwait(false);
            return 4;
        }

        var root = Path.GetFullPath(model);
        for (var round = 1; round <= rounds; round++)
        {
            var cache = Path.Combine(Path.GetTempPath(), "maquettiste-time-load-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ModelStore(new EngineOptions { RepoRoot = root, CacheDirectory = cache });
                await using (store.ConfigureAwait(false))
                {
                    var clock = Stopwatch.StartNew();
                    var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
                    var load = clock.Elapsed;
                    clock.Restart();
                    var nodes = snapshot.Localization.Nodes.Count;
                    var index = clock.Elapsed;
                    clock.Restart();
                    var completeness = snapshot.Localization.Completeness();
                    var pass = clock.Elapsed;
                    var shards = snapshot.Localization.Shards.Count;
                    var missing = completeness.Sum(c => c.Missing);
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"round {round}: {snapshot.Documents.Count} elements, {snapshot.Localization.Locales.Count} locales, {shards} shards; load {load.TotalMilliseconds:F0} ms, localizable nodes {index.TotalMilliseconds:F0} ms ({nodes}), completeness {pass.TotalMilliseconds:F0} ms ({missing} missing), total {(load + index + pass).TotalMilliseconds:F0} ms"))
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (Directory.Exists(cache))
                    Directory.Delete(cache, recursive: true);
            }
        }

        return 0;
    }
}
