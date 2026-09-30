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
    public const string Usage = "usage: Maquettiste.Bench time-load --model <dir> [--rounds 3] [--warm-cache]\n";

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
        var warm = false;
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--model": model = value; i++; break;
                case "--warm-cache": warm = true; break;
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
        // --warm-cache: one cache folder for every round, filled by an untimed round 0, so each round is a restart of the host
        // over its cache volume (a new model store, nothing in memory) rather than a first open.
        var shared = warm ? Path.Combine(Path.GetTempPath(), "maquettiste-time-load-" + Guid.NewGuid().ToString("N")) : null;
        for (var round = warm ? 0 : 1; round <= rounds; round++)
        {
            var cache = shared ?? Path.Combine(Path.GetTempPath(), "maquettiste-time-load-" + Guid.NewGuid().ToString("N"));
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
                        $"round {(round == 0 ? "0 (fills the cache)" : round)}{(warm && round > 0 ? " warm cache" : "")}: {snapshot.Documents.Count} elements, {snapshot.Localization.Locales.Count} locales, {shards} shards; load {load.TotalMilliseconds:F0} ms, localizable nodes {index.TotalMilliseconds:F0} ms ({nodes}), completeness {pass.TotalMilliseconds:F0} ms ({missing} missing), total {(load + index + pass).TotalMilliseconds:F0} ms{(warm ? $"; shard cache {ShardFiles(cache)} files" : "")}"))
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if ((!warm || round == rounds) && Directory.Exists(cache))
                    Directory.Delete(cache, recursive: true);
            }
        }

        return 0;
    }

    /// <summary>The parsed-shard cache files under a cache folder (the engine's <c>shards/</c>), which a warm round reads.</summary>
    private static int ShardFiles(string cache)
    {
        var folder = Path.Combine(cache, "shards");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.bin").Length : 0;
    }
}
